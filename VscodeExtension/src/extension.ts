import * as vscode from 'vscode';
import * as path    from 'path';
import * as fs      from 'fs';
import { SqlarFs }  from './SqlarFs';

const PATHS_KEY = 'worlddb.paths';

export async function activate(ctx: vscode.ExtensionContext): Promise<void> {
    // sql.js is pure WASM — no native module, no electron-rebuild needed
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const initSqlJs = require('sql.js');
    const SQL = await initSqlJs({
        locateFile: (f: string) =>
            path.join(ctx.extensionPath, 'node_modules', 'sql.js', 'dist', f),
    });

    const sqlarFs = new SqlarFs(SQL);
    ctx.subscriptions.push(sqlarFs);

    ctx.subscriptions.push(
        vscode.workspace.registerFileSystemProvider('worlddb', sqlarFs, {
            isCaseSensitive: true,
        })
    );

    // Reattach any DBs that were open in a previous session
    const stored: Record<string, string> = ctx.globalState.get(PATHS_KEY, {});
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        if (folder.uri.scheme !== 'worlddb') continue;
        const dbPath = stored[folder.uri.authority];
        if (dbPath && fs.existsSync(dbPath)) {
            sqlarFs.registerDb(folder.uri.authority, dbPath);
        } else {
            vscode.window.showWarningMessage(
                `worlddb: '${folder.name}' could not be reopened — run "World DB: Open" to reconnect.`
            );
        }
    }

    ctx.subscriptions.push(
        vscode.commands.registerCommand('worlddb.open', async () => {
            const picked = await vscode.window.showOpenDialog({
                title:    'Open world.db',
                filters:  { 'SQLite DB': ['db'], 'All files': ['*'] },
                canSelectMany: false,
            });
            if (!picked?.length) return;

            const dbPath    = picked[0].fsPath;
            const authority = path
                .basename(dbPath, path.extname(dbPath))
                .toLowerCase()
                .replace(/[^a-z0-9]/g, '-');

            try {
                sqlarFs.registerDb(authority, dbPath);
            } catch (err) {
                vscode.window.showErrorMessage(`worlddb: failed to open "${dbPath}": ${err}`);
                return;
            }

            // Persist authority → file path for next session
            const existing: Record<string, string> = ctx.globalState.get(PATHS_KEY, {});
            await ctx.globalState.update(PATHS_KEY, { ...existing, [authority]: dbPath });

            vscode.workspace.updateWorkspaceFolders(
                vscode.workspace.workspaceFolders?.length ?? 0,
                0,
                {
                    uri:  vscode.Uri.from({ scheme: 'worlddb', authority, path: '/' }),
                    name: `WorldDB: ${path.basename(dbPath)}`,
                }
            );
        })
    );
}

export function deactivate(): void {}
