"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const vscode = __importStar(require("vscode"));
const path = __importStar(require("path"));
const fs = __importStar(require("fs"));
const SqlarFs_1 = require("./SqlarFs");
const PATHS_KEY = 'worlddb.paths';
async function activate(ctx) {
    // sql.js is pure WASM — no native module, no electron-rebuild needed
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const initSqlJs = require('sql.js');
    const SQL = await initSqlJs({
        locateFile: (f) => path.join(ctx.extensionPath, 'node_modules', 'sql.js', 'dist', f),
    });
    const sqlarFs = new SqlarFs_1.SqlarFs(SQL);
    ctx.subscriptions.push(sqlarFs);
    ctx.subscriptions.push(vscode.workspace.registerFileSystemProvider('worlddb', sqlarFs, {
        isCaseSensitive: true,
    }));
    // Reattach any DBs that were open in a previous session
    const stored = ctx.globalState.get(PATHS_KEY, {});
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        if (folder.uri.scheme !== 'worlddb')
            continue;
        const dbPath = stored[folder.uri.authority];
        if (dbPath && fs.existsSync(dbPath)) {
            sqlarFs.registerDb(folder.uri.authority, dbPath);
        }
        else {
            vscode.window.showWarningMessage(`worlddb: '${folder.name}' could not be reopened — run "World DB: Open" to reconnect.`);
        }
    }
    ctx.subscriptions.push(vscode.commands.registerCommand('worlddb.open', async () => {
        const picked = await vscode.window.showOpenDialog({
            title: 'Open world.db',
            filters: { 'SQLite DB': ['db'], 'All files': ['*'] },
            canSelectMany: false,
        });
        if (!picked?.length)
            return;
        const dbPath = picked[0].fsPath;
        const authority = path
            .basename(dbPath, path.extname(dbPath))
            .toLowerCase()
            .replace(/[^a-z0-9]/g, '-');
        try {
            sqlarFs.registerDb(authority, dbPath);
        }
        catch (err) {
            vscode.window.showErrorMessage(`worlddb: failed to open "${dbPath}": ${err}`);
            return;
        }
        // Persist authority → file path for next session
        const existing = ctx.globalState.get(PATHS_KEY, {});
        await ctx.globalState.update(PATHS_KEY, { ...existing, [authority]: dbPath });
        vscode.workspace.updateWorkspaceFolders(vscode.workspace.workspaceFolders?.length ?? 0, 0, {
            uri: vscode.Uri.from({ scheme: 'worlddb', authority, path: '/' }),
            name: `WorldDB: ${path.basename(dbPath)}`,
        });
    }));
}
function deactivate() { }
//# sourceMappingURL=extension.js.map