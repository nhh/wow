using System.Globalization;
using Microsoft.Data.Sqlite;
using Shared.Scripts;

namespace Server;

public sealed class WorldDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public int   ParticleCount      { get; set; }
    public float MoveSpeed          { get; private set; }
    public float WorldRadius        { get; private set; }
    public float JumpSpeed          { get; private set; }
    public float Gravity            { get; private set; }
    public float PlayerViewRadius   { get; private set; }
    public float ParticleViewRadius { get; private set; }
    public float DRThreshold        { get; private set; }

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "world.db");

    public WorldDatabase(string? path = null)
    {
        path ??= DefaultPath;
        _conn = new SqliteConnection($"Data Source={path}");
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
        ("particle_count",       "1000"),
        ("move_speed",           "5"),
        ("world_radius",         "30"),
        ("jump_speed",           "9"),
        ("gravity",              "20"),
        ("player_view_radius",   "100"),
        ("particle_view_radius", "70"),
        ("dr_threshold",         "0.15"),
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
        cmd.CommandText = """
            INSERT OR IGNORE INTO sqlar (name, mode, mtime, sz, data)
            VALUES ('scripts/particle.cs', 0, 0, 0, CAST($src AS BLOB))
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$src", ParticleScriptSource);
        cmd.ExecuteNonQuery();
    }

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
    }

    private void LoadConfig()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM game_config";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Apply(r.GetString(0), r.GetString(1));

        Console.WriteLine($"[db] particle_count={ParticleCount} move_speed={MoveSpeed} " +
                          $"jump_speed={JumpSpeed} gravity={Gravity} " +
                          $"player_view={PlayerViewRadius} particle_view={ParticleViewRadius}");
    }

    private void Apply(string key, string raw)
    {
        float F() => float.Parse(raw, CultureInfo.InvariantCulture);
        switch (key)
        {
            case "particle_count":       ParticleCount      = int.Parse(raw);  break;
            case "move_speed":           MoveSpeed          = F();             break;
            case "world_radius":         WorldRadius        = F();             break;
            case "jump_speed":           JumpSpeed          = F();             break;
            case "gravity":              Gravity            = F();             break;
            case "player_view_radius":   PlayerViewRadius   = F();             break;
            case "particle_view_radius": ParticleViewRadius = F();             break;
            case "dr_threshold":         DRThreshold        = F();             break;
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public IParticleScript LoadParticleScript()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT CAST(data AS TEXT) FROM sqlar WHERE name = 'scripts/particle.cs'";
        var source = cmd.ExecuteScalar() as string;

        if (source is null)
        {
            Console.WriteLine("[db] scripts/particle.cs not found — compiling built-in source");
            source = ParticleScriptSource;
        }

        Console.WriteLine("[db] compiling scripts/particle.cs ...");
        var script = ScriptCompiler.Compile<IParticleScript>(source, "Scripts.ParticleScript");
        Console.WriteLine("[db] scripts/particle.cs compiled OK");
        return script;
    }

    public GameObjectInstance[] LoadGameObjects()
    {
        // Collect all defs with their script sources
        var defs    = new List<(uint id, float x, float y, float z, float yaw, string? source)>();
        var sources = new Dictionary<string, string>(); // scriptName → C# source

        using (var scriptCmd = _conn.CreateCommand())
        {
            scriptCmd.CommandText = "SELECT CAST(data AS TEXT) FROM sqlar WHERE name = $n";
            var nameParam = scriptCmd.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Text);

            using var defCmd = _conn.CreateCommand();
            defCmd.CommandText = "SELECT id, x, y, z, yaw, script_name FROM game_object_defs";
            using var r = defCmd.ExecuteReader();
            while (r.Read())
            {
                uint  id         = (uint)r.GetInt64(0);
                float x          = (float)r.GetDouble(1);
                float y          = (float)r.GetDouble(2);
                float z          = (float)r.GetDouble(3);
                float yaw        = (float)r.GetDouble(4);
                string scriptName = r.GetString(5);

                if (!sources.ContainsKey(scriptName))
                {
                    nameParam.Value = scriptName;
                    var src = scriptCmd.ExecuteScalar() as string;
                    if (src is null)
                    {
                        Console.WriteLine($"[db] warning: script '{scriptName}' not found in sqlar, skipping GO {id}");
                        defs.Add((id, x, y, z, yaw, null));
                        continue;
                    }
                    sources[scriptName] = src;
                }
                defs.Add((id, x, y, z, yaw, sources[scriptName]));
            }
        }

        // Each GO gets its own compiled script instance (own private state)
        var validDefs = defs.Where(d => d.source is not null).ToList();
        if (validDefs.Count == 0) return [];

        var entries  = validDefs.Select(d => (d.source!, "Scripts.GameObjectScript")).ToList();
        var compiled = ScriptCompiler.CompileAll<Shared.Scripts.IGameObjectScript>(entries);

        var instances = new GameObjectInstance[validDefs.Count];
        for (int i = 0; i < validDefs.Count; i++)
        {
            var (id, x, y, z, yaw, _) = validDefs[i];
            instances[i] = new GameObjectInstance(id, x, y, z, yaw, compiled[i]);
        }

        Console.WriteLine($"[db] loaded {instances.Length} game object(s)");
        return instances;
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

        public class ParticleScript : IParticleScript
        {
            private const float FormationDuration = 9f;
            private const float BlendDuration     = 2f;
            private const int   FormationCount    = 5;

            public void Update(uint tick, float t, Span<ParticleSnapshot> positions)
            {
                int   n        = positions.Length;
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
                    positions[i] = new ParticleSnapshot { X = ax, Y = ay, Z = az, ColorId = ac };
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
