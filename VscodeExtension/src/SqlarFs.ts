import * as vscode from 'vscode';
import { readFileSync, writeFileSync } from 'fs';
import type { SqlJsStatic, Database } from 'sql.js';
import * as yaml from 'js-yaml';

interface DbEntry {
    db:        Database;
    fsPath:    string;
    saveTimer: ReturnType<typeof setTimeout> | null;
    tables:    Set<string>;   // non-sqlar tables, cached on open
}

const enc = new TextEncoder();

function toBytes(v: unknown): Uint8Array {
    if (v instanceof Uint8Array) return v;
    if (typeof v === 'string')   return enc.encode(v);
    return new Uint8Array(0);
}

export class SqlarFs implements vscode.FileSystemProvider, vscode.Disposable {
    private readonly _sql:     SqlJsStatic;
    private readonly _dbs    = new Map<string, DbEntry>();
    private readonly _emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
    readonly onDidChangeFile   = this._emitter.event;

    constructor(sql: SqlJsStatic) {
        this._sql = sql;
    }

    registerDb(authority: string, fsPath: string): void {
        const existing = this._dbs.get(authority);
        if (existing) {
            this._flushNow(authority);
            existing.db.close();
        }
        const db     = new this._sql.Database(readFileSync(fsPath));
        const tables = this._queryTables(db);
        this._dbs.set(authority, { db, fsPath, saveTimer: null, tables });
    }

    // ── table helpers ─────────────────────────────────────────────────────

    private _queryTables(db: Database): Set<string> {
        const results = db.exec(
            "SELECT name FROM sqlite_master WHERE type='table' " +
            "AND name NOT LIKE 'sqlite_%' AND name != 'sqlar'"
        );
        const names = new Set<string>();
        if (results.length) {
            for (const [name] of results[0].values) names.add(name as string);
        }
        return names;
    }

    // "game_config.yaml" → "game_config" if it's a known table, else null
    private _tableFor(entry: DbEntry, path: string): string | null {
        if (!path.endsWith('.yaml')) return null;
        const name = path.slice(0, -5);
        return entry.tables.has(name) ? name : null;
    }

    private _renderTable(db: Database, tableName: string): string {
        const res = db.exec(`SELECT * FROM "${tableName}"`);
        if (!res.length || !res[0].values.length) return '[]\n';
        const { columns, values } = res[0];
        const rows = values.map(row => {
            const obj: Record<string, unknown> = {};
            columns.forEach((col, i) => { obj[col] = row[i]; });
            return obj;
        });
        return yaml.dump(rows, { lineWidth: 120, noRefs: true, quotingType: '"' });
    }

    private _writeTable(db: Database, tableName: string, content: string): void {
        let parsed: unknown;
        try { parsed = yaml.load(content); }
        catch (e) { throw new Error(`YAML parse error: ${e}`); }
        if (!Array.isArray(parsed)) return;

        // column names from PRAGMA — values: [cid, name, type, notnull, dflt_value, pk]
        const pragma = db.exec(`PRAGMA table_info("${tableName}")`);
        if (!pragma.length) return;
        const cols         = pragma[0].values.map(r => r[1] as string);
        const quotedCols   = cols.map(c => `"${c}"`).join(', ');
        const placeholders = cols.map(() => '?').join(', ');

        db.run('BEGIN');
        try {
            db.run(`DELETE FROM "${tableName}"`);
            const stmt = db.prepare(
                `INSERT OR REPLACE INTO "${tableName}" (${quotedCols}) VALUES (${placeholders})`
            );
            for (const row of parsed as Record<string, unknown>[]) {
                stmt.run(cols.map(c => {
                    const v = row[c];
                    return (v === undefined || v === null) ? null : v as string | number;
                }));
            }
            stmt.free();
            db.run('COMMIT');
        } catch (e) {
            db.run('ROLLBACK');
            throw e;
        }
    }

    // ── internals ─────────────────────────────────────────────────────────

    private _entry(authority: string): DbEntry {
        const e = this._dbs.get(authority);
        if (!e) throw vscode.FileSystemError.Unavailable(
            `worlddb: no DB for '${authority}' — run "World DB: Open"`
        );
        return e;
    }

    private _scheduleSave(authority: string): void {
        const e = this._dbs.get(authority);
        if (!e) return;
        if (e.saveTimer) clearTimeout(e.saveTimer);
        e.saveTimer = setTimeout(() => this._flushNow(authority), 500);
    }

    private _flushNow(authority: string): void {
        const e = this._dbs.get(authority);
        if (!e) return;
        if (e.saveTimer) { clearTimeout(e.saveTimer); e.saveTimer = null; }
        writeFileSync(e.fsPath, e.db.export());
    }

    // ── FileSystemProvider ────────────────────────────────────────────────

    watch(): vscode.Disposable { return { dispose: () => {} }; }

    stat(uri: vscode.Uri): vscode.FileStat {
        const path = uri.path.replace(/^\//, '');
        if (!path) return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };

        const entry = this._entry(uri.authority);

        // table YAML?
        if (this._tableFor(entry, path) !== null) {
            const bytes = enc.encode(this._renderTable(entry.db, this._tableFor(entry, path)!));
            return { type: vscode.FileType.File, ctime: 0, mtime: 0, size: bytes.byteLength };
        }

        // exact sqlar file?
        const fstmt = entry.db.prepare(
            'SELECT mtime, length(data) AS size FROM sqlar WHERE name = ?'
        );
        fstmt.bind([path]);
        if (fstmt.step()) {
            const row = fstmt.get() as [number, number];
            fstmt.free();
            return { type: vscode.FileType.File, ctime: 0, mtime: (row[0] ?? 0) * 1000, size: row[1] ?? 0 };
        }
        fstmt.free();

        // implied sqlar directory?
        const dstmt = entry.db.prepare(
            "SELECT count(*) FROM sqlar WHERE name LIKE ? ESCAPE '\\'"
        );
        dstmt.bind([path.replace(/[%_\\]/g, '\\$&') + '/%']);
        dstmt.step();
        const cnt = (dstmt.get() as [number])[0] ?? 0;
        dstmt.free();
        if (cnt > 0) return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };

        throw vscode.FileSystemError.FileNotFound(uri);
    }

    readDirectory(uri: vscode.Uri): [string, vscode.FileType][] {
        const entry  = this._entry(uri.authority);
        const dir    = uri.path.replace(/^\//, '');
        const prefix = dir ? dir + '/' : '';

        const result = new Map<string, vscode.FileType>();

        // table YAMLs only appear at root
        if (!dir) {
            for (const tableName of entry.tables) {
                result.set(`${tableName}.yaml`, vscode.FileType.File);
            }
        }

        // sqlar entries
        const stmt = entry.db.prepare('SELECT name FROM sqlar ORDER BY name');
        while (stmt.step()) {
            const name = (stmt.get() as [string])[0];
            if (!name?.startsWith(prefix)) continue;
            const rest  = name.slice(prefix.length);
            const slash = rest.indexOf('/');
            if (slash === -1) result.set(rest, vscode.FileType.File);
            else              result.set(rest.slice(0, slash), vscode.FileType.Directory);
        }
        stmt.free();

        return [...result.entries()];
    }

    readFile(uri: vscode.Uri): Uint8Array {
        const path  = uri.path.replace(/^\//, '');
        const entry = this._entry(uri.authority);

        // table YAML?
        const tableName = this._tableFor(entry, path);
        if (tableName !== null) {
            return enc.encode(this._renderTable(entry.db, tableName));
        }

        // sqlar
        const stmt = entry.db.prepare('SELECT data FROM sqlar WHERE name = ?');
        stmt.bind([path]);
        if (!stmt.step()) { stmt.free(); throw vscode.FileSystemError.FileNotFound(uri); }
        const raw = (stmt.get() as [unknown])[0];
        stmt.free();
        return toBytes(raw);
    }

    writeFile(uri: vscode.Uri, content: Uint8Array, _opts: { create: boolean; overwrite: boolean }): void {
        const path  = uri.path.replace(/^\//, '');
        const entry = this._entry(uri.authority);

        // table YAML?
        const tableName = this._tableFor(entry, path);
        if (tableName !== null) {
            this._writeTable(entry.db, tableName, new TextDecoder().decode(content));
            this._scheduleSave(uri.authority);
            this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
            return;
        }

        // sqlar
        const now = Math.floor(Date.now() / 1000);
        entry.db.run(`
            INSERT INTO sqlar (name, mode, mtime, sz, data)
            VALUES (?, 0, ?, ?, ?)
            ON CONFLICT(name) DO UPDATE
                SET data  = excluded.data,
                    mtime = excluded.mtime,
                    sz    = excluded.sz
        `, [path, now, content.byteLength, content]);
        this._scheduleSave(uri.authority);
        this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
    }

    delete(uri: vscode.Uri, options: { recursive: boolean }): void {
        const path  = uri.path.replace(/^\//, '');
        const entry = this._entry(uri.authority);

        if (this._tableFor(entry, path) !== null) {
            throw vscode.FileSystemError.NoPermissions('Table YAML files cannot be deleted');
        }

        const esc = path.replace(/[%_\\]/g, '\\$&');
        if (options.recursive) {
            entry.db.run(
                "DELETE FROM sqlar WHERE name = ? OR name LIKE ? ESCAPE '\\'",
                [path, esc + '/%']
            );
        } else {
            entry.db.run('DELETE FROM sqlar WHERE name = ?', [path]);
        }
        this._scheduleSave(uri.authority);
        this._emitter.fire([{ type: vscode.FileChangeType.Deleted, uri }]);
    }

    rename(oldUri: vscode.Uri, newUri: vscode.Uri, _opts: { overwrite: boolean }): void {
        const oldPath = oldUri.path.replace(/^\//, '');
        const entry   = this._entry(oldUri.authority);

        if (this._tableFor(entry, oldPath) !== null) {
            throw vscode.FileSystemError.NoPermissions('Table YAML files cannot be renamed');
        }

        const newPath = newUri.path.replace(/^\//, '');
        entry.db.run('UPDATE sqlar SET name = ? WHERE name = ?', [newPath, oldPath]);
        this._scheduleSave(oldUri.authority);
        this._emitter.fire([
            { type: vscode.FileChangeType.Deleted, uri: oldUri },
            { type: vscode.FileChangeType.Created, uri: newUri },
        ]);
    }

    createDirectory(_uri: vscode.Uri): void {
        // SQLAR uses path prefixes — no explicit directory entries needed
    }

    dispose(): void {
        for (const [auth] of this._dbs) this._flushNow(auth);
        this._emitter.dispose();
        for (const { db } of this._dbs.values()) db.close();
        this._dbs.clear();
    }
}
