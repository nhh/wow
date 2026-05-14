using System.Globalization;
using Microsoft.Data.Sqlite;
using Shared;

namespace Server;

public sealed class WorldDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public float MoveSpeed          { get; private set; }
    public float WorldRadius        { get; private set; }
    public float JumpSpeed          { get; private set; }
    public float Gravity            { get; private set; }
    public float PlayerViewRadius   { get; private set; }
    public float ParticleViewRadius { get; private set; }
    public float DRThreshold        { get; private set; }
    public float  GoViewRadius  { get; private set; }
    public string ScriptsFolder { get; set; } = "";

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "world.db");

    public WorldDatabase(string? path = null)
    {
        path         ??= DefaultPath;
        ScriptsFolder  = Path.Combine(Path.GetDirectoryName(path)!, "compiled-scripts");
        _conn          = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        EnsureSchema();
        SeedDefaults();
        LoadConfig();
    }

    private void EnsureSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS game_config (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS zones (
                id    INTEGER PRIMARY KEY,
                name  TEXT    NOT NULL,
                min_x REAL    DEFAULT -500,
                max_x REAL    DEFAULT  500,
                min_z REAL    DEFAULT -500,
                max_z REAL    DEFAULT  500
            );
            CREATE TABLE IF NOT EXISTS spawn_points (
                id      INTEGER PRIMARY KEY,
                zone_id INTEGER REFERENCES zones(id),
                x       REAL    DEFAULT 0,
                y       REAL    DEFAULT 0,
                z       REAL    DEFAULT 0,
                yaw     REAL    DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS sqlar (
                name  TEXT PRIMARY KEY,
                mode  INTEGER,
                mtime INTEGER,
                sz    INTEGER,
                data  BLOB
            );
            CREATE TABLE IF NOT EXISTS game_object_defs (
                id          INTEGER PRIMARY KEY,
                type_name   TEXT    NOT NULL,
                x           REAL    DEFAULT 0,
                y           REAL    DEFAULT 0,
                z           REAL    DEFAULT 0,
                yaw         REAL    DEFAULT 0,
                script_name TEXT    NOT NULL REFERENCES sqlar(name)
            );
            """);
    }

    private static readonly (string key, string value)[] Defaults =
    [
        ("move_speed",           "5"),
        ("world_radius",         "30"),
        ("jump_speed",           "9"),
        ("gravity",              "20"),
        ("player_view_radius",   "100"),
        ("particle_view_radius", "70"),
        ("dr_threshold",         "0.15"),
        ("go_view_radius",       "100"),
    ];

    private void SeedDefaults()
    {
        using var tx  = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO game_config (key, value) VALUES ($k, $v)";
        var pk = cmd.Parameters.Add("$k", SqliteType.Text);
        var pv = cmd.Parameters.Add("$v", SqliteType.Text);
        foreach (var (k, v) in Defaults)
        {
            pk.Value = k;
            pv.Value = v;
            cmd.ExecuteNonQuery();
        }
        Execute("INSERT OR IGNORE INTO zones (id, name) VALUES (1, 'default')");
        SeedParticleScript(cmd);
        SeedGameObjectScript(cmd);
        tx.Commit();
    }

    private static void SeedParticleScript(SqliteCommand cmd)
    {
        // OR REPLACE so existing databases get the updated IGameObjectScript version
        cmd.CommandText = """
            INSERT OR REPLACE INTO sqlar (name, mode, mtime, sz, data)
            VALUES ('scripts/particle.cs', 0, 0, 0, CAST($src AS BLOB))
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$src", ParticleScriptSource);
        cmd.ExecuteNonQuery();
    }

    private const int ParticleSeedCount = 1000;

    private static void SeedGameObjectScript(SqliteCommand cmd)
    {
        cmd.CommandText = """
            INSERT OR IGNORE INTO sqlar (name, mode, mtime, sz, data)
            VALUES ('scripts/go/spinning_box.cs', 0, 0, 0, CAST($src AS BLOB))
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$src", GameObjectScriptSource);
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            INSERT OR IGNORE INTO game_object_defs (id, type_name, x, y, z, yaw, script_name)
            VALUES (1, 'SpinningBox', 5, 0, 5, 0, 'scripts/go/spinning_box.cs')
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();

        // Seed particle instances — script sets position/color each tick via UpdateBulk
        cmd.CommandText = """
            INSERT OR IGNORE INTO game_object_defs (id, type_name, x, y, z, yaw, script_name)
            VALUES ($id, 'Particle', 0, 0, 0, 0, 'scripts/particle.cs')
            """;
        var pid = cmd.Parameters.Add("$id", SqliteType.Integer);
        for (int i = 0; i < ParticleSeedCount; i++)
        {
            pid.Value = 1001 + i;
            cmd.ExecuteNonQuery();
        }
    }

    private void LoadConfig()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM game_config";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Apply(r.GetString(0), r.GetString(1));

        Console.WriteLine($"[db] move_speed={MoveSpeed} jump_speed={JumpSpeed} gravity={Gravity} " +
                          $"player_view={PlayerViewRadius} go_view={GoViewRadius}");
    }

    private void Apply(string key, string raw)
    {
        float F() => float.Parse(raw, CultureInfo.InvariantCulture);
        switch (key)
        {
            case "move_speed":           MoveSpeed          = F();             break;
            case "world_radius":         WorldRadius        = F();             break;
            case "jump_speed":           JumpSpeed          = F();             break;
            case "gravity":              Gravity            = F();             break;
            case "player_view_radius":   PlayerViewRadius   = F();             break;
            case "particle_view_radius": ParticleViewRadius = F();             break;
            case "dr_threshold":         DRThreshold        = F();             break;
            case "go_view_radius":       GoViewRadius       = F();             break;
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public GameObjectGroup[] LoadGameObjects()
    {
        // Pass 1: load all defs, group by script_name
        var byScript = new Dictionary<string, List<GameObjectState>>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, x, y, z, yaw, script_name FROM game_object_defs";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string scriptName = r.GetString(5);
            if (!byScript.ContainsKey(scriptName))
                byScript[scriptName] = [];
            byScript[scriptName].Add(new GameObjectState
            {
                Id  = (uint)r.GetInt64(0),
                X   = (float)r.GetDouble(1),
                Y   = (float)r.GetDouble(2),
                Z   = (float)r.GetDouble(3),
                Yaw = (float)r.GetDouble(4),
            });
        }

        if (byScript.Count == 0) return [];

        // Pass 2: load one DLL + create one script instance per unique script_name
        var groups = new List<GameObjectGroup>(byScript.Count);
        foreach (var (scriptName, states) in byScript)
        {
            var dllPath = Path.Combine(ScriptsFolder, Path.ChangeExtension(scriptName, ".dll"));
            if (!File.Exists(dllPath))
            {
                Console.WriteLine($"[db] warning: {dllPath} not found, skipping {states.Count} GO(s)");
                continue;
            }
            var type   = ScriptLoader.LoadType<Shared.Scripts.IGameObjectScript>(dllPath);
            var script = ScriptLoader.CreateInstance<Shared.Scripts.IGameObjectScript>(type);
            groups.Add(new GameObjectGroup(script, states.ToArray()));
            Console.WriteLine($"[db] group '{scriptName}': {states.Count} instance(s)");
        }

        Console.WriteLine($"[db] {groups.Sum(g => g.States.Length)} game object(s) in {groups.Count} group(s)");
        return groups.ToArray();
    }

    public void Dispose() => _conn.Dispose();

    private const string GameObjectScriptSource = """
        using System;
        using Shared;
        using Shared.Scripts;

        namespace Scripts;

        public class GameObjectScript : IGameObjectScript
        {
            public void Update(uint tick, float t, ref GameObjectState state)
            {
                state.Yaw = t * 1.0f;
                state.Y   = 0.5f + MathF.Sin(t * 2f) * 0.3f;
            }
        }
        """;

    private const string ParticleScriptSource = """
        using System;
        using Shared;
        using Shared.Scripts;

        namespace Scripts;

        public class ParticleScript : IGameObjectScript
        {
            private const float FormationDuration = 9f;
            private const float BlendDuration     = 2f;
            private const int   FormationCount    = 5;

            public void Update(uint tick, float t, ref GameObjectState state) { }

            public void UpdateBulk(uint tick, float t, Span<GameObjectState> states)
            {
                int   n        = states.Length;
                float cycleLen = FormationDuration * FormationCount;
                float cyclePos = t % cycleLen;
                int   formIdx  = (int)(cyclePos / FormationDuration);
                float formT    = cyclePos - formIdx * FormationDuration;

                float blend = 0f;
                if (formT >= FormationDuration - BlendDuration)
                    blend = Smoothstep((formT - (FormationDuration - BlendDuration)) / BlendDuration);

                int nextIdx = (formIdx + 1) % FormationCount;

                for (int i = 0; i < n; i++)
                {
                    GetPos(formIdx, i, t, n, out float ax, out float ay, out float az, out byte ac);
                    if (blend > 0f)
                    {
                        GetPos(nextIdx, i, t, n, out float bx, out float by, out float bz, out byte bc);
                        ax = ax + (bx - ax) * blend;
                        ay = ay + (by - ay) * blend;
                        az = az + (bz - az) * blend;
                        if (blend > 0.5f) ac = bc;
                    }
                    states[i].X       = ax;
                    states[i].Y       = ay;
                    states[i].Z       = az;
                    states[i].ColorId = ac;
                }
            }

            private static float Smoothstep(float x) => x * x * (3f - 2f * x);

            private static void GetPos(int f, int i, float t, int n,
                out float x, out float y, out float z, out byte colorId)
            {
                switch (f)
                {
                    case 0: Sphere    (i, t, n, out x, out y, out z, out colorId); break;
                    case 1: DoubleHelix(i, t, n, out x, out y, out z, out colorId); break;
                    case 2: WaveGrid  (i, t, n, out x, out y, out z, out colorId); break;
                    case 3: Vortex    (i, t, n, out x, out y, out z, out colorId); break;
                    default: Galaxy   (i, t, n, out x, out y, out z, out colorId); break;
                }
            }

            private static void Sphere(int i, float t, int n,
                out float x, out float y, out float z, out byte colorId)
            {
                float phi   = MathF.Acos(1f - 2f * (i + 0.5f) / n);
                float theta = MathF.PI * (1f + MathF.Sqrt(5f)) * i + t * 0.4f;
                float r     = 13f;
                x = r * MathF.Sin(phi) * MathF.Cos(theta);
                y = r * MathF.Cos(phi) + 13f;
                z = r * MathF.Sin(phi) * MathF.Sin(theta);
                colorId = (byte)(i * 8 / n + 1);
            }

            private static void DoubleHelix(int i, float t, int n,
                out float x, out float y, out float z, out byte colorId)
            {
                int   strand = i & 1;
                float frac   = (i >> 1) / (float)(n >> 1);
                float angle  = frac * MathF.PI * 12f + t * 1.8f + strand * MathF.PI;
                float radius = 5f + MathF.Sin(frac * MathF.PI * 4f) * 1.5f;
                x = MathF.Cos(angle) * radius;
                y = frac * 22f;
                z = MathF.Sin(angle) * radius;
                colorId = (byte)(strand == 0 ? 1 : 5);
            }

            private static void WaveGrid(int i, float t, int n,
                out float x, out float y, out float z, out byte colorId)
            {
                int cols = (int)MathF.Sqrt(n);
                int rows = n / cols;
                int row  = i / cols;
                int col  = i % cols;
                float fx = col / (float)(cols - 1);
                float fz = row / (float)Math.Max(rows - 1, 1);
                x = (fx - 0.5f) * 44f;
                z = (fz - 0.5f) * 44f;
                y = MathF.Sin(fx * MathF.PI * 5f + t * 2.5f) * 5f
                  + MathF.Sin(fz * MathF.PI * 4f + t * 1.8f) * 5f
                  + MathF.Sin((fx + fz) * MathF.PI * 3f + t)  * 2f + 10f;
                colorId = (byte)(Math.Clamp((int)((y - 2f) / 2f), 0, 7) + 1);
            }

            private static void Vortex(int i, float t, int n,
                out float x, out float y, out float z, out byte colorId)
            {
                float frac   = i / (float)n;
                float height = frac * 22f;
                float radius = 1.5f + frac * 11f;
                float speed  = 4f - frac * 2.5f;
                float angle  = frac * MathF.PI * 24f + t * speed;
                x = MathF.Cos(angle) * radius;
                y = height;
                z = MathF.Sin(angle) * radius;
                colorId = (byte)((int)(frac * 7f) + 1);
            }

            private static void Galaxy(int i, float t, int n,
                out float x, out float y, out float z, out byte colorId)
            {
                int   arm      = i % 3;
                float frac     = (i / 3) / (float)(n / 3);
                float armAngle = arm * (MathF.Tau / 3f);
                float radius   = 1.5f + frac * 19f;
                float angle    = frac * MathF.PI * 7f + armAngle + t * 0.35f;
                float scatter  = MathF.Sin(frac * 11f + t * 0.7f + arm) * 1.2f;
                x = MathF.Cos(angle) * radius + scatter;
                y = 9f + MathF.Sin(frac * MathF.PI * 3f + t + arm) * 1.5f;
                z = MathF.Sin(angle) * radius + scatter;
                colorId = (byte)(arm + 2);
            }
        }
        """;
}
