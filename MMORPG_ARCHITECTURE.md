# Modern MMORPG — Architektur-Konzept

> Zielgruppe: Senior Engineers. Kein Tutorial, keine Erklärung von Grundbegriffen.  
> Fokus: Die 10 größten architekturellen Entscheidungen, ihre Tradeoffs, und warum die gewählte Option gewinnt.

---

## 1. Transport Layer

### Entscheidung: LiteNetLib (UDP) — QUIC zurückgestellt

> **POC-Stand:** `System.Net.Quic` wurde evaluiert und verworfen. MsQuic auf Windows puffert `WriteAsync` intern und ignoriert `FlushAsync()` auf bidirektionalen Streams — Server-seitiges `ReadExactlyAsync` blockiert dauerhaft. Mehrere Workarounds (unidirektionale Streams, zwei bidirektionale) scheiterten an Deadlocks beim Accept-Loop. Ersatz: **LiteNetLib 2.1.3** (message-oriented UDP, kein Buffering-Problem).

Klassische MMOs nutzen TCP für zuverlässige Pakete (State, Events) und einen separaten UDP-Socket für Positionen. Das erfordert doppeltes NAT-Traversal, zwei Verbindungen pro Client, und eigene Reliability-Layer über UDP.

**Ziel-Architektur (QUIC) — für Produktion:**

| Feature | TCP+UDP | QUIC |
|---------|---------|------|
| Multiplexed Streams | ✗ (HOL-Blocking) | ✓ (unabhängige Streams) |
| Unreliable Datagrams | ✗ (zweiter Socket) | ✓ (QUIC Datagrams RFC 9221) |
| 0-RTT Reconnect | ✗ | ✓ |
| Connection Migration | ✗ | ✓ (IP-Wechsel transparent) |
| Verschlüsselung | Optional | Immer (TLS 1.3) |

**POC-Kanalstruktur (LiteNetLib):**

```
LiteNetLib Connection (UDP, ein Socket)
├─ DeliveryMethod.Unreliable        → CInputState (Movement-Input)
└─ DeliveryMethod.ReliableOrdered   → SWorldSnapshot, SParticleSnapshot, SWelcome
```

Position-Snapshots gehen aktuell reliable — im POC akzeptabel. Für Produktion: Snapshots auf Unreliable umstellen, Events auf separatem ReliableOrdered-Kanal.

**Serialisierung: MemoryPack + raw structs**

Da Server und Client beide .NET 10 sind, entfällt jede Cross-Language-Anforderung.

- **Fixe Messages** (PositionSnapshot, die meisten Game-Events): `MemoryMarshal.Write/Read<T>()` — zero-copy, zero-alloc, kein Library-Overhead
- **Variable Messages** (Chat, Inventory, komplexe Events): `MemoryPack` — source-generated, kein Reflection, schneller als FlatBuffers auf .NET
- **Schema**: C#-Typdefinition im Shared-Projekt IS das Schema — keine `.fbs`-Dateien, kein Codegen-Schritt, keine Synchronisierungsfehler

---

## 2. Entity Model: ECS

### Entscheidung: Pure ECS, keine OOP-Hierarchie

OOP-Hierarchie (`Player extends Unit extends Object`) führt zu:
- Diamond-Inheritance oder Composition-Workarounds
- Cache-Misses: Virtuelle Dispatch, scattered heap allocation
- God-Objects: `Player` mit 200 Methoden

**ECS (Entity Component System):**

```
Entity   = uint64 (GUID, kein Objekt)
Component = pure data struct, keine Methoden
System    = Logik, operiert auf Component-Queries
```

Beispiel:

```go
// Components — nur Daten
type Position struct { X, Y, Z, O float32 }
type Health   struct { Current, Max int32 }
type Faction  struct { ID uint16 }
type AIState  struct { Target EntityID; Phase uint8 }

// System — nur Logik
func (s *CombatSystem) Tick(dt float32) {
    for _, e := range s.world.Query(Health{}, Faction{}) {
        // ...
    }
}
```

**Vorteile für MMORPGs konkret:**
- Creature und Player teilen exakt dieselben Systeme (Combat, Aura, Movement) ohne Vererbung
- Komponenten sind linear im Speicher → SIMD-freundlich
- Hot-reload von Systemen zur Laufzeit möglich
- Einfaches Serialisieren: Component = Struct = Wire-Format

**Welche Daten gehören nicht ins ECS:**
- Persistente Daten (Inventar, Quests, Account): on-disk SQLite
- Temporäre Netzwerk-State (ACK-Sequenzen, Subscription-Sets): in-memory SQLite (`sessions`-Tabelle)
- Räumliche Queries, Visibility-Sets, AI-Joins: in-memory SQLite (Details in Abschnitt 11)

ECS-Arrays sind der Write-Puffer innerhalb eines Ticks. SQLite ist der State-Store am Tick-Ende.

---

## 3. State-Synchronisation

### Entscheidung: Ereignisbasiertes Modell + Positions-Snapshots + Delta-from-ACK

Drei verschiedene Datenkategorien brauchen drei verschiedene Strategien:

#### 3a. Logischer State: Immutable Events

Statt `UNIT_FIELD_HEALTH = 4500` schickt der Server:

```csharp
// shared/messages/Events.cs
[MemoryPackable]
[MemoryPackUnion(0, typeof(DamageEvent))]
[MemoryPackUnion(1, typeof(CastEvent))]
[MemoryPackUnion(2, typeof(DeathEvent))]
partial abstract class ServerEvent { public uint Tick { get; init; } }

[MemoryPackable]
partial class DamageEvent : ServerEvent
{
    public ulong Source   { get; init; }
    public ulong Target   { get; init; }
    public int   Amount   { get; init; }
    public uint  SpellId  { get; init; }
    public int   ResultHp { get; init; } // redundant, ermöglicht Reconnect ohne Full-State-Request
}
```

Der Client leitet Health selbst ab. Die C#-Klasse im Shared-Projekt ist gleichzeitig Wire-Format, Typdefinition und Dokumentation — keine separate Schema-Datei.

**Vorteile:**
- Client kann Events für Prediction vorwegnehmen (Spell gecastet → Schaden erwartet)
- Vollständiger Replay für Debugging
- Events sind die Source of Truth — Datenbank speichert Events, nicht Snapshots

#### 3b. Position: Unreliable Snapshots

```csharp
// shared/messages/Snapshots.cs
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct PositionSnapshot  // 28 Bytes, blittable
{
    public ulong  EntityId;
    public float  X, Y, Z, O;
    public ushort Seq;    // Sequenznummer für Jitter-Buffer
    public ushort Flags;  // swimming, falling, ...
}
```

Server sendet 20 Hz. Client interpoliert zwischen den letzten zwei empfangenen Snapshots mit 100ms Jitter-Buffer-Delay. Alte Pakete (seq < letzter verarbeiteter) werden verworfen.

#### 3c. Wire-Kompression: Delta-from-last-ACK

Für Events und State-Updates über reliable Streams:

```
Server hält pro Client:
  lastAckedTick    uint
  sentButUnacked   map[tick]StateDelta

Client sendet regelmäßig: ACK(lastProcessedTick)
Server schickt Delta(lastAckedTick → currentTick)
```

Paketverlust ist kein Problem: Delta enthält alles seit dem letzten bestätigten State. Kein Divergenz-Risiko wie bei simplen Diffs.

---

## 4. Client-Side Prediction & Server Reconciliation

### Entscheidung: Vollständige Prediction für lokalen Spieler, keine Prediction für andere Entities

**Warum Prediction notwendig ist:**

Bei 100ms RTT fühlt sich jede Eingabe träge an wenn man auf Server-Bestätigung wartet. Input Lag ist der größte Komfortfaktor in Online-Games.

**Algorithmus (identisch zu Quake/Source/Overwatch):**

```
Client:
  1. Erhält Inputs (WASD, Maus)
  2. Speichert Input mit lokalem Tick: inputHistory[tick] = Input
  3. Simuliert Bewegung lokal sofort → Rendering zeigt Ergebnis
  4. Schickt Input an Server
  5. Erhält Server-State für Tick N zurück
  6. Falls Client-State[N] ≠ Server-State[N]: Rollback + Replay
     ├─ Setze State auf Server-State[N]
     └─ Re-simuliere alle Inputs von Tick N bis currentTick
```

**Was der Client NICHT predicted:**
- Andere Spieler (kein Zugriff auf deren Inputs)
- Creature-Bewegung (Server-Authority)
- Spell-Ergebnisse (anti-cheat)

**Andere Spieler: Interpolation statt Extrapolation**

Interpolation = Rendern zwischen zwei bekannten Positionen (Vergangenheit).  
Extrapolation = Vorhersagen basierend auf letzter Velocity (Zukunft).

Interpolation ist stabiler. Preis: ~100ms künstlicher Lag auf anderen Spielern. Das ist akzeptabel und für MMORPGs Standard.

---

## 5. Interest Management

### Entscheidung: Relevancy-Score mit Bandwidth-Budget — Distanz + Blickfeld, nicht pure Distanz

Pure Distanz-PvS (wie in WoW 3.3.5a) hat Probleme:
- In dicht besiedelten Zonen (City, Raid) explodiert die Bandwidth
- Alle Entities behandelt gleich: Boss 50m entfernt = neutral Critter 49m entfernt
- Entities hinter dem Spieler erhalten dasselbe Update-Intervall wie Entities direkt vor ihm

#### 5a. Sichtfeld-Komponente (FOV-Bias)

Entitäten im Blickfeld des Spielers sind unmittelbar wahrnehmbar und brauchen hohe Update-Frequenz. Entitäten hinter dem Spieler können mit reduziertem Intervall oder größerer Distanzgrenze gesendet werden — der Spieler sieht sie physisch nicht, bevor er sich umdreht.

```
Beobachter-Yaw: φ  (Blickrichtung, Einheitsvektor: (sin φ, cos φ))
Vektor Beobachter → Entity: d⃗ (normiert)

dot = sin(φ)·dx + cos(φ)·dz      // [-1, +1]
fov_factor = (dot + 1) / 2        // [0, 1]: 1 = direkt vor, 0 = direkt hinter

Beispiel bei dist=60m:
  Entity direkt vor Spieler (dot≈1):   fov_factor = 1.0
  Entity seitlich (dot≈0):             fov_factor = 0.5
  Entity direkt hinter Spieler (dot≈-1): fov_factor = 0.0
```

FOV-Faktor modifiziert **effektive Distanz**, nicht den Score direkt — so bleibt die Distanzgrenze interpretierbar:

```
effective_dist = raw_dist / (0.3 + 0.7 * fov_factor)
// Entity 80m hinter Spieler → effective_dist = 80 / 0.3 = 267m → außerhalb Radius
// Entity 80m vor  Spieler  → effective_dist = 80 / 1.0 =  80m → innerhalb Radius
```

Der Faktor `0.3` stellt sicher, dass auch Entities hinter dem Spieler nicht vollständig ignoriert werden — ein Spieler der sich umdreht soll Entities nicht poppen sehen.

#### 5b. Vollständiger Relevancy-Score

```
score = f(effective_dist, entityTyp, combatRelevanz, priorität)

Beispiel:
  Boss im eigenen Raid (combat, direkt vor):    score = 1.0  → immer senden
  PvP-Gegner 80m, direkt vor Spieler:           score = 0.8  → hohes Update-Intervall
  PvP-Gegner 80m, direkt hinter Spieler:        score = 0.3  → niedriges Intervall
  Ambient-Creature 60m vor Spieler:             score = 0.3  → niedriges Intervall
  Ambient-Creature 60m hinter Spieler:          score = 0.0  → kein Update
  Ambient-Creature 150m (egal wohin):           score = 0.0  → kein Update
```

**Wichtige Ausnahme:** Entities in aktiver Kampfinteraktion mit dem Beobachter erhalten immer `score = 1.0`, unabhängig von Winkel oder Distanz — ein Gegner der den Spieler von hinten angreift muss korrekt synchronisiert sein.

#### 5c. Bandwidth-Budget per Client

```
Pro Tick wird der Relevancy-Sort nach Score absteigend durchlaufen.
Entities werden in den Send-Buffer aufgenommen bis Budget erschöpft.
Übrige Entities: Update-Intervall erhöhen (Position alle 200ms statt 50ms).
```

Das ist die Technik aus Valve's Source Engine (entity priority system) und Improbable SpatialOS.

#### 5d. Räumlicher Index: SQLite R-Tree

SQLite hat eine eingebaute R-Tree Extension die handgestrickten Grid- oder BVH-Code vollständig ersetzt. Der R-Tree liefert den Distanz-Kandidaten-Pool, der FOV-Filter läuft danach in C#:

```sql
-- Schritt 1: R-Tree liefert alle Entities im Worst-Case-Radius (ohne FOV)
SELECT id, x, z, yaw FROM entity_rtree
JOIN positions USING (id)
WHERE min_x >= $px-100 AND max_x <= $px+100
  AND min_y >= $pz-100 AND max_y <= $pz+100;
```

```csharp
// Schritt 2: FOV-Filter + Score in C# (kein SQL-Overhead für dot-product)
foreach (var candidate in rtreeResults)
{
    float dx = candidate.X - observer.X;
    float dz = candidate.Z - observer.Z;
    float rawDist = MathF.Sqrt(dx*dx + dz*dz);

    float dot = MathF.Sin(observer.Yaw) * (dx/rawDist)
              + MathF.Cos(observer.Yaw) * (dz/rawDist);
    float fovFactor  = (dot + 1f) / 2f;
    float effectDist = rawDist / (0.3f + 0.7f * fovFactor);

    if (effectDist > ViewRadius) continue;
    float score = ComputeScore(effectDist, candidate, observer);
    sendList.Add((candidate, score));
}
sendList.Sort(...); // absteigend nach Score
```

Der R-Tree schneidet den Suchraum auf O(log N + k) Kandidaten, der FOV-Filter ist O(k) mit k ≪ N. Keine eigene Datenstruktur nötig.

#### 5e. Skalierbarkeitsanalyse

**Geometrische Auswirkung des FOV-Bias:**

Der effektive Sichtradius variiert je nach Winkel zur Blickrichtung:

```
r_raw(θ) = R × (0.3 + 0.35 × (cos(θ) + 1))

θ=0°   (direkt vorne):  r = R × 1.00 = 100m
θ=45°:                  r = R × 0.90 =  90m
θ=90°  (seitlich):      r = R × 0.65 =  65m
θ=135°:                 r = R × 0.40 =  40m
θ=180° (direkt hinten): r = R × 0.30 =  30m
```

Integration über alle Winkel ergibt die sichtbare Fläche:

```
A_fov  = ∫₀²π r(θ)²/2 dθ = R²π × 0.484
A_kreis = R²π

→ FOV-Bias reduziert die sichtbare Fläche auf ~48 % des reinen Radius-Kreises.
  Jeder Client sieht im Schnitt halb so viele Entities wie bei reinem Distanz-Culling.
```

**Kumulativer Skalierungsgewinn** (Gigabit-Server, ViewRadius=100m, 1 km² Welt, 1.000 Partikel):

| Stufe | Sichtbare Spieler/Client (N=1.000) | Spieler-BW | Partikel-BW | Gesamt @20Hz | Limit |
|---|---|---|---|---|---|
| Broadcast (kein Culling) | 999 | O(N²) | O(N) | ~300 MB/s | **~350 Sessions** |
| Radius-Culling (R=100m) | ~31 | O(N·k) | O(N) | ~80 MB/s | **~2.000 Sessions** |
| Radius + FOV-Bias | ~15 | O(N·k/2) | O(N) | ~67 MB/s | **~3.500 Sessions** |
| Radius + FOV + Partikel-Culling | ~15 | O(N·k/2) | O(N·0.48) | ~38 MB/s | **~6.000+ Sessions** |

Partikel-Culling wendet denselben FOV-gefilterten Radius auf den Partikel-Snapshot an: jeder Client bekommt nur Partikel im sichtbaren Bereich, was die Partikel-Bandbreite ebenfalls auf ~48 % reduziert.

**Stufenweise Skalierungshebel zusammengefasst:**

```
Broadcast → Radius-Culling:      ~6×  (O(N²) → O(N·k))
+ FOV-Bias:                      ~2×  (k → k/2)
+ Partikel-Culling:              ~2×  (13 KB → 6 KB/Client)
────────────────────────────────────────────────────────
Gesamtgewinn:                   ~24×  gegenüber reinem Broadcast
```

Die Skalierungsgrenze liegt danach nicht mehr bei der Bandbreite sondern beim CPU-Overhead des R-Tree-Lookups pro Session (O(N log N) pro Tick) und beim Kernel-`writev()`-Overhead für viele kleine UDP-Pakete.

---

## 6. World Partitioning

### Entscheidung: Seamless World mit Zone-Servern + Cross-Zone Entity Presence

**Zonen-Server-Modell:**

```
WorldCoordinator (stateless, routing only)
├─ ZoneServer A  (Elwynn Forest: 0,0 → 2000,2000)
├─ ZoneServer B  (Stormwind: 1800,1800 → 2500,2500)  ← overlap mit A
├─ ZoneServer C  (Dungeon Instance Pool)
└─ ZoneServer D  (Battleground Pool)
```

Zonen überlappen in Grenz-Bereichen. Ein Spieler nahe der Grenze ist bei beiden Zonen registriert — der Handoff ist transparent.

**Seamless Zone Transfer:**

```
1. Client nähert sich Zonengrenze (< 200m)
2. WorldCoordinator authorisiert Transfer
3. Client verbindet QUIC-Stream zur neuen Zone (parallel zur alten)
4. Neue Zone schickt Bootstrap-State
5. Alte Zone gibt Authority ab → neue Zone übernimmt
6. Client disconnectet alte Zone
```

Der Spieler merkt nichts, kein Ladebalken.

**Instanzen:** Eigene ZoneServer aus Pool, on-demand spawnen (Kubernetes/Nomad).  
**Kreuzzone-Chat/Gruppen/Raid:** Message-Broker (NATS JetStream), nicht direkt zwischen Zone-Servern.

---

## 7. Server-Topologie

### Entscheidung: Vertikale Skalierung pro Zone + Horizontale Service-Schicht

```
                    ┌─────────────────────────────────┐
                    │         API Gateway (QUIC)       │
                    │    Auth, Rate-Limit, Routing      │
                    └──────────────┬──────────────────┘
                                   │
            ┌──────────────────────┼──────────────────────┐
            │                      │                      │
     ┌──────▼──────┐        ┌──────▼──────┐        ┌──────▼──────┐
     │  ZoneServer  │        │  ZoneServer  │        │  ZoneServer  │
     │  (stateful)  │        │  (stateful)  │        │  (stateful)  │
     │  Game Loop   │        │  Game Loop   │        │  Game Loop   │
     └──────┬───────┘        └──────┬───────┘        └─────────────┘
            │                      │
            └──────────┬───────────┘
                       │  NATS JetStream (Event Bus)
                       │
        ┌──────────────┼──────────────────────────┐
        │              │                           │
  ┌─────▼─────┐  ┌─────▼──────┐           ┌──────▼──────┐
  │  Chat Svc  │  │  Guild Svc  │           │  Auction Svc │
  │ (stateless)│  │ (stateless) │           │ (stateless)  │
  └─────┬──────┘  └─────┬───────┘           └──────┬───────┘
        │               │                           │
        └───────────────┴───────────────────────────┘
                        │
                ┌───────▼────────┐
                │   PostgreSQL   │
                │  (Event Store  │
                │  + Read Models)│
                └────────────────┘
```

**ZoneServer ist stateful, .NET 10, single-threaded pro Tick.**  
Keine Microservice-Zerlegung der Spiellogik — distributed Game-Logic ist das teuerste Problem in MMORPGs (Latenz zwischen Services für jeden Kampf-Schritt).

**Zustandslose Services** (Chat, Gilden, Auktionshaus) kommunizieren über Event Bus, nicht direkt mit ZoneServern.

### Stack: .NET 10 auf Server und Client

Server und Client sind beide C# / .NET 10. Das ist die zentrale Stack-Entscheidung.

**Geteilter Code (Shared-Projekt im Monorepo):**

```
/shared
  /physics        SimulateMovement(), CollisionCheck()
  /messages       ServerEvent, DamageEvent, PositionSnapshot, ...  (Wire-Format = C#-Typ)
  /math           Vector3, Quaternion, BoundingBox
  /ecs            Component-Definitionen (Position, Health, AIState, ...)
  /constants      SpellIds, FactionIds, MapIds
```

Der Bewegungsvalidierungs-Code auf dem Server **ist dieselbe Funktion** wie die Client-Side-Prediction. Prediction-Bugs durch Divergenz zwischen Client- und Server-Physik sind strukturell ausgeschlossen.

```csharp
// shared/physics/Movement.cs — läuft auf Client UND Server
public static Position Simulate(Position pos, InputState input, float dt)
{
    // Physik, Speed-Cap, Kollision
}

// Server: Anti-Cheat-Validation
var expected = Movement.Simulate(lastPos, receivedInput, dt);
if (Vector3.DistanceSquared(expected.XYZ, claimed.XYZ) > Tolerance)
    RejectAndSnapBack(session);

// Client: Prediction — gleiche Funktion, kein Drift möglich
var predicted = Movement.Simulate(localPos, myInput, dt);
```

**Concurrency auf dem Server:**

Go-Goroutines werden durch `System.Threading.Channels` + async/await ersetzt:

```csharp
// Session-owned State: ein Channel pro Session
Channel<ISessionOp> _opQueue = Channel.CreateUnbounded<ISessionOp>();

// Owner-Loop: sequenziell, kein Lock nötig
await foreach (var op in _opQueue.Reader.ReadAllAsync())
    await op.ApplyAsync(this);
```

Das Session-per-Channel-Pattern ist funktional identisch zum goroutine-basierten Modell, nur mit .NET-Primitiven.

**GC-Konfiguration für den Server:**

```csharp
// Program.cs — vor dem Game-Loop
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

// Im Tick-kritischen Pfad zusätzlich:
GC.TryStartNoGCRegion(8 * 1024 * 1024); // 8 MB Budget
RunTick();
GC.EndNoGCRegion();
```

`SustainedLowLatency` weist den GC an, keine Generation-2-Collections während der Laufzeit zu erzwingen. Pauses bleiben < 1 ms — unkritisch bei 50 ms Tick-Budget.

**Deployment:** `dotnet publish -r linux-x64 -p:PublishAot=true` → single binary, kein .NET-Runtime-Install auf dem Server nötig. Gleiche Deployment-Eigenschaften wie ein Go-Binary.

---

## 8. Persistence: Event Sourcing + CQRS

### Entscheidung: Events als Source of Truth, Read Models für Queries

Traditionell: ORM → Spieler-Row mit current_hp, gold, x, y, z.  
Problem: Was ist passiert? Warum hat der Spieler dieses Item? Support-Anfragen unlösbar.

**Event Sourcing:**

```
EventStore (append-only):
  PlayerCreated      { account_id, name, race, class }
  ItemAcquired       { player_id, item_id, source: "loot", source_id: 1234 }
  ItemLooted         { player_id, item_id, creature_id }
  GoldChanged        { player_id, delta: +500, source: "vendor_sell" }
  LevelUp            { player_id, new_level: 42 }
  ...
```

Aktueller Spieler-State = Event-Replay vom letzten Snapshot.  
Snapshots alle N Events für schnellen Login.

**CQRS Read Models:**

```
Leaderboard-DB     ← projiziert aus LevelUp-Events
AuctionHouse-DB    ← projiziert aus ItemAcquired/ItemSold-Events
SupportTool-DB     ← vollständige Event-History per Spieler
```

**Vorteile konkret:**
- Support kann jeden Item-Ursprung nachvollziehen
- Rollback bei Server-Bug: Events bis Zeitpunkt X replizen
- Neue Features: neues Read Model aus bestehenden Events ableiten ohne Migration
- Natürliche Passung zu immutable Events auf dem Wire

---

## 9. Anti-Cheat

### Entscheidung: Server-Authority als einziges echtes Anti-Cheat

Client-seitige Anti-Cheat-Software (BattlEye, VAC) ist Perimeter-Security — sie hält ehrliche Leute draußen, entschlossene Cheater kaum.

**Server-Authority-Prinzipien:**

| Was der Client schickt | Was der Server akzeptiert |
|------------------------|--------------------------|
| Bewegungs-Input (WASD) | Nur wenn physikalisch plausibel (max Speed, keine Teleports) |
| Spell-Cast-Request | Nur wenn: Range OK, Mana ausreichend, Cooldown abgelaufen, LoS frei |
| Loot-Request | Nur wenn: Creature tot, Spieler in Range, Loot-Owner |
| Schaden-Wert | Niemals — Server berechnet Schaden vollständig |

**Plausibilitäts-Checks für Bewegung:**

```
maxSpeedSq   = (baseSpeed * speedMultiplier)² * (dt * 1.1)  // 10% Toleranz für Jitter
actualDeltaSq = (newPos - lastPos)²

if actualDeltaSq > maxSpeedSq {
    // Speedhack oder extreme Latenz
    // 1. Warnung → 2. Snap-Back → 3. Ban
}
```

**Was nicht geht:** Komplexe Heuristiken (AimBot-Detection, Pattern-Matching). Das ist ein Wettrüsten das Server-Entwickler verlieren. Fokus auf: Kann der Spieler das physikalisch getan haben? Wenn nein: ablehnen.

---

## 10. Client-Architektur

### Stack: .NET 9 + Silk.NET

**Technologie-Entscheidung: .NET mit Silk.NET**

Silk.NET ist ein Low-Level-.NET-Binding für Vulkan, OpenGL, GLFW, OpenAL und weitere native APIs — kein Managed-Abstraktions-Layer, sondern direkte Bindung mit Source-Generator-generierten Stubs ohne Overhead.

| Concern | Bibliothek |
|---------|------------|
| Rendering | `Silk.NET.Vulkan` |
| Windowing / Input | `Silk.NET.Windowing` + `Silk.NET.Input` (GLFW) |
| Audio | `Silk.NET.OpenAL` |
| Transport (POC) | `LiteNetLib 2.1.3` (UDP, message-oriented, kein MsQuic-Bug) |
| Transport (Prod) | `System.Net.Quic` (.NET 10, nutzt msquic) |
| Serialisierung (variabel) | `MemoryPack` (NuGet) |
| Serialisierung (fix/blittable) | `MemoryMarshal` (BCL, kein NuGet) |
| ECS | `Arch` (Chunk-basiert, SIMD-kompatibel) |
| SIMD (Simulation) | `System.Numerics` — `Vector128<T>`, `Vector256<T>` |
| Debug-UI | `Silk.NET.ImGui` |

### Schichtmodell

```
┌─────────────────────────────────────────────────────────────┐
│                     Rendering Layer                          │
│  Silk.NET.Vulkan — GPU-driven, Bindless Resources           │
│  Mesh-Streaming, LOD, Frustum-Culling auf GPU               │
└──────────────────────────┬──────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────┐
│                  Presentation Layer                          │
│  Interpolation (andere Entities), Animation State Machine   │
│  Silk.NET.OpenAL (Audio), Reactive Signals (UI)             │
└──────────────────────────┬──────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────┐
│                  Simulation Layer                            │
│  Arch ECS — Chunk-Layout, System.Numerics SIMD              │
│  Lokale Physik, Collision, Client-Side Prediction           │
│  Input-History-Buffer (stackalloc), Rollback                │
└──────────────────────────┬──────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────┐
│                   Network Layer                              │
│  LiteNetLib (UDP) — Unreliable Inputs, Reliable Snapshots   │
│  Datagram-Jitter-Buffer, Delta-from-ACK, Reconnect          │
│  MemoryPack (Events) + MemoryMarshal (Snapshots)            │
└─────────────────────────────────────────────────────────────┘
```

### Das GC-Problem und wie .NET es löst

Der Garbage Collector ist die einzige substanzielle Gefahr für Frame-Consistency in .NET. Ein GC-Pause von 5 ms im Tick-kritischen Pfad ist ein sichtbarer Hitch.

**Mitigation-Strategie: Allocation-freier Tick-Pfad**

Das Ziel ist nicht "kein GC" — sondern "kein Heap-Alloc im Game-Loop":

```csharp
// ArrayPool statt new[] für temporäre Puffer
var buffer = ArrayPool<byte>.Shared.Rent(4096);
try { /* ... */ }
finally { ArrayPool<byte>.Shared.Return(buffer); }

// stackalloc für kleine temporäre Structs (kein GC-Druck)
Span<PositionDelta> deltas = stackalloc PositionDelta[128];

// System.Numerics: SIMD ohne Alloc
Vector128<float> pos = Vector128.Create(x, y, z, o);

// GC am Tick-Anfang einfrieren (Budget: vorab gemessen)
GC.TryStartNoGCRegion(4 * 1024 * 1024);
RunTick();
GC.EndNoGCRegion();
```

**Arch ECS: Chunk-Layout verhindert GC-Pressure**

Arch alloziert Entities in vorab-allozierten Chunks (`Component[]` im Managed Heap, nie einzeln). Das bedeutet: keine Per-Entity-Allokation im Tick, der GC sieht keine neuen Objekte.

```csharp
// Arch Query: iteriert direkt über Chunk-Memory, kein Alloc
world.Query(new QueryDescription().WithAll<Position, Velocity>(),
    (ref Position pos, ref Velocity vel) => {
        pos.X += vel.X * dt;
        pos.Y += vel.Y * dt;
    });
```

**NativeAOT (optional):**  
`dotnet publish -r win-x64 -p:PublishAot=true` kompiliert den Client Ahead-of-Time. Eliminiert JIT-Warmup-Hitches in den ersten Frames und reduziert Memory-Footprint. Reflection muss durch Source-Generator ersetzt werden — Silk.NET und MemoryPack sind beide Source-Generator-basiert und NativeAOT-kompatibel.

### QUIC in .NET

`System.Net.Quic` ist seit .NET 9 production-ready (nutzt msquic nativ):

```csharp
// Reliable Stream — Events, Login, Inventory
await using var eventStream = await connection.OpenOutboundStreamAsync(
    QuicStreamType.Bidirectional);

// Unreliable Datagrams — Position-Snapshots
await connection.SendDatagramAsync(positionBuffer);

// Receive-Loop Position-Snapshot: MemoryMarshal, zero-copy, zero-alloc
var datagram = await connection.ReceiveDatagramAsync();
var snapshots = MemoryMarshal.Cast<byte, PositionSnapshot>(datagram.Span[1..]);

// Receive-Loop Event: MemoryPack deserialize
var evt = MemoryPackSerializer.Deserialize<ServerEvent>(eventSpan);
```

### UI-Architektur: Reactive Signals

Keine Polling-Updates, kein Zugriff des UI-Codes auf ECS-State. Events aus dem Simulation-Layer triggern Signals, UI subscribed:

```csharp
// Signal-Definition
Signal<int> PlayerHealth = new(maxHp);

// Simulation-Layer schreibt
PlayerHealth.Set(newHp);

// UI subscribed (Presentation Layer, eigener Thread)
PlayerHealth.Subscribe(hp => healthBar.Value = hp / (float)maxHp);
```

**Asset Streaming:** Kein Loading Screen nach Initial-Load. Assets (Texturen, Meshes, Audio) werden spatial gestreamt — der Client hält einen Radius-Cache im RAM, prefetcht basierend auf Bewegungsrichtung. Silk.NET.Vulkan erlaubt async Texture-Upload über Transfer-Queue parallel zur Render-Queue.

---

## 11. In-Memory SQLite als Zone-State-Layer

### Entscheidung: SQLite in-memory für Zone-State, ECS-Arrays als Tick-interner Write-Puffer

Die intuitive Ablehnung ("zu langsam, row-orientiert, single-writer") hält einer quantitativen Analyse nicht stand.

### Write-Intensität pro Tick

Annahmen: 100 Spieler, 500 Creatures, 600 Entities total.

| System | Frequenz | Schreibende Entities | Write-Volumen/Tick |
|--------|----------|---------------------|-------------------|
| MovementSystem | 20 Hz | ~100 bewegen sich | 100 × 16 Bytes = 1,6 KB |
| CombatSystem | 10 Hz | ~120 (20% in Combat) | 120 × 12 Bytes = 1,4 KB |
| AuraSystem | 1 Hz | ~250 aktive Auren | 250 × 4 Bytes = 1,0 KB |
| AISystem | 5 Hz | ~500 | ≤ 4 KB (meist 0) |
| **Simulation gesamt** | | | **~5 KB/Tick** |
| **Netzwerk-Output** | 20 Hz | alle sichtbaren Paare | **~200 KB/Tick** |

Der Netzwerk-Output übersteigt den gesamten Simulation-State um Faktor 40. Der eigentliche Engpass ist `writev()` in den Kernel-Buffer — keine Datenbank-Wahl ändert das.

**SQLite-Overhead bei realistischer Last:**

```
100 Position-UPDATEs in einer Transaktion: ~100 × 0,1 µs = 10 µs
Tick-Budget bei 20 Hz:                                    50.000 µs
SQLite-Overhead:                                             0,02%
```

Der Schlüssel ist die Transaktion. SQLite's Overhead sitzt beim Commit, nicht pro Statement. Ein ganzer Tick als eine Transaktion macht den Overhead irrelevant.

**SIMD-Argumente greifen bei MMO-Skala nicht:**

SIMD-Gains auf float32-Arrays materialisieren ab ~10.000 Entities im selben tight loop. Bei einer typischen Zone (< 1.000 Entities) liegt der Unterschied im µs-Bereich, nicht im ms-Bereich. Kein architekturelles Argument.

### Tick-Pattern: Write-Collect + Single Transaction

Systeme akkumulieren Änderungen in lokalen Structs (lock-free), am Tick-Ende eine einzige Transaktion:

```
Tick Start
  ├─ MovementSystem  → []positionDelta  (lock-free)
  ├─ CombatSystem    → []statsDelta     (lock-free)
  ├─ AISystem        → []aiDelta        (lock-free)
  └─ Tick End:
       tx.Begin()
       bulk INSERT/UPDATE aus allen Delta-Slices
       tx.Commit()   ← einzige Write-Lock-Phase, ~10 µs
```

Das löst das Single-Writer-Problem: SQLite's Schreibsperre sitzt nur für die Commit-Phase, nicht für die Tick-Laufzeit. Systeme laufen parallel ohne Contention.

### Schema

```sql
CREATE TABLE entities (
    id     INTEGER PRIMARY KEY,
    type   INTEGER NOT NULL,    -- Player=1, Creature=2, GameObject=3
    map_id INTEGER NOT NULL,
    flags  INTEGER DEFAULT 0
);

CREATE TABLE positions (
    entity_id INTEGER PRIMARY KEY REFERENCES entities(id),
    x REAL, y REAL, z REAL, o REAL
);

-- R-Tree: Spatial-Index, ersetzt handgestrickten Grid-Code vollständig
CREATE VIRTUAL TABLE entity_rtree USING rtree(
    id, min_x, max_x, min_y, max_y
);

CREATE TABLE stats (
    entity_id INTEGER PRIMARY KEY REFERENCES entities(id),
    hp INTEGER, hp_max INTEGER,
    mp INTEGER, mp_max INTEGER
);

CREATE TABLE auras (
    entity_id    INTEGER REFERENCES entities(id),
    spell_id     INTEGER,
    expires_tick INTEGER,
    stacks       INTEGER DEFAULT 1,
    PRIMARY KEY (entity_id, spell_id)
);

CREATE TABLE ai_state (
    entity_id      INTEGER PRIMARY KEY REFERENCES entities(id),
    target_id      INTEGER,
    phase          INTEGER DEFAULT 0,
    last_path_tick INTEGER
);

-- Ersetzt visibleCreatures/visiblePlayers/visibleGameObjects-Maps
CREATE TABLE visibility (
    observer_id INTEGER REFERENCES entities(id),
    entity_id   INTEGER REFERENCES entities(id),
    PRIMARY KEY (observer_id, entity_id)
);

CREATE TABLE sessions (
    entity_id     INTEGER PRIMARY KEY REFERENCES entities(id),
    last_ack_tick INTEGER,
    addr          TEXT
);
```

### Aufteilung: ECS-Array vs. SQLite

| State | Frequenz | Speicher | Begründung |
|-------|----------|----------|------------|
| Positions (Tick-intern) | 20 Hz | ECS-Array → Commit | Systeme lesen lokal, kein SQL-Overhead im Tick |
| R-Tree (Spatial-Index) | 20 Hz update | SQLite | Eingebaute, bewährte Implementierung |
| Health / Mana | 10 Hz | SQLite | Write-Volumen problemlos |
| Auras / Buffs | 1 Hz | SQLite | Ablauf-Checks als SQL-Query |
| AI-State | 5 Hz | SQLite | Selten geändert, häufig gejoint |
| Visibility-Sets | 5 Hz | SQLite | Ersetzt drei separate Go-Maps |
| Session-Metadaten | selten | SQLite | ACK-Tracking, Addr |
| Inventar, Quests | sehr selten | on-disk SQLite | Kein Zone-State |
| Events (Schaden, Tod) | async append | on-disk SQLite | Event Store |

### Queries die als SQL expressiv werden

Komplexe Operationen die heute Custom-Code erfordern, werden zu Joins:

```sql
-- Visibility-Sweep: alle Entities in Sichtradius
SELECT id FROM entity_rtree
WHERE min_x >= $px-100 AND max_x <= $px+100
  AND min_y >= $py-100 AND max_y <= $py+100;

-- Aggro: feindliche Entities in 40m
SELECT e.id FROM entity_rtree r
JOIN entities e ON e.id = r.id
WHERE r.min_x >= $px-40 AND r.max_x <= $px+40
  AND r.min_y >= $py-40 AND r.max_y <= $py+40
  AND e.faction != $my_faction;

-- Aura-Ablauf am Tick-Ende
SELECT entity_id, spell_id FROM auras
WHERE expires_tick <= $current_tick;
```

### Inspektierbarkeit im Betrieb

Während eines Live-Bugs lässt sich der komplette Zone-State ohne Instrumentierungs-Code abfragen:

```sql
-- Entities mit negativem HP (sollte leer sein)
SELECT e.id, s.hp FROM entities e
JOIN stats s ON s.entity_id = e.id
WHERE s.hp < 0;

-- Spieler ohne Visibility-Eintrag (möglicher State-Bug)
SELECT e.id FROM entities e
WHERE e.type = 1
  AND NOT EXISTS (SELECT 1 FROM visibility v WHERE v.observer_id = e.id);
```

### Persistence ist strukturell gelöst

```go
srcDB.Backup(dstDB)  // in-memory → on-disk, atomar, ohne Tick-Unterbrechung
```

Kein eigenes Serialisierungsformat, kein Save/Load-Code, kein Snapshot-Protokoll.

### Skalierungsgrenze

Bei ~2.000+ aktiven Entities in einer Zone wird der SQLite-Commit für Positions messbar (~1 ms/Tick statt 10 µs). Ab dieser Schwelle: Positionen zurück in reine ECS-Arrays, alle anderen Tabellen bleiben in SQLite. Das ist eine isolierte Optimierung, kein Architekturwechsel — weil die Tick-interne ECS-Array-Zwischenstufe von Anfang an vorhanden ist.

---

## Entscheidungsmatrix

| Bereich | Gewählt | Verworfen | Hauptgrund |
|---------|---------|-----------|------------|
| Transport (POC) | LiteNetLib/UDP | System.Net.Quic | MsQuic-Buffering-Bug auf Windows blockiert bidirektionale Streams |
| Transport (Prod) | QUIC | TCP+UDP | Ein Socket, 0-RTT, keine HOL-Blocks |
| Serialisierung (variabel) | MemoryPack | FlatBuffers / Protobuf | .NET-native, Source-Gen, schneller auf .NET |
| Serialisierung (fix) | MemoryMarshal (BCL) | MemoryPack | Blittable Structs brauchen keinen Serializer |
| Entity Model | ECS | OOP-Hierarchie | Cache-Lokalität, keine God-Objects |
| Logical State | Immutable Events | Mutable Fields | Prediction, Replay, Audit |
| Positions | Unreliable Snapshots | Reliable Updates | Latenz wichtiger als Vollständigkeit |
| Wire-Kompression | Delta-from-ACK | Simple Diffs | Paketverlust-sicher |
| Spatial Index | SQLite R-Tree | BVH / Fixed Grid | Bewährt, kein eigener Index-Code |
| Zone-State | SQLite in-memory | ECS-Only / Custom Maps | Inspektierbar, Persistence gratis, Queries als SQL |
| Interest Mgmt | Relevancy + Budget | Pure Distanz | Bandwidth-Kontrolle |
| World | Seamless + Zone-Overlap | Hard Zone-Borders | Keine Ladescreens |
| Persistence | Event Sourcing | ORM | Auditierbarkeit, Rollback |
| Anti-Cheat | Server-Authority | Client-Software | Einzige verlässliche Methode |
| Client Prediction | Vollständig (eigener Char) | Warten auf Server | Input Lag inakzeptabel |
| Server + Client Stack | .NET 10 (beide) | Go + C# split | Code-Sharing: Physik, Schema, ECS-Components |
| Client Stack | Silk.NET (Vulkan, GLFW, OpenAL) | Unity / Unreal | Low-Level-Kontrolle, kein Engine-Overhead |
| Client Rendering | Silk.NET.Vulkan | OpenGL / DX11 | Bindless, GPU-driven, Cross-Platform |
| Client ECS | Arch | Custom / OOP | Chunk-Layout, kein GC-Druck, SIMD-kompatibel |
| Client Transport (POC) | LiteNetLib | System.Net.Quic | MsQuic-Bug auf Windows; LiteNetLib message-oriented, kein Buffering |
| GC-Strategie | Allocation-freier Tick + NoGCRegion | Managed Allocs | Frame-Hitches durch GC eliminieren |

---

## 12. POC: Bewegbarer Cube + Server-Lasttest

### Ziel

Einen lauffähigen Prototypen erstellen der den gesamten Architektur-Stack von Abschnitt 1–11 in Miniatur verifiziert:

- **Client**: Silk.NET-Fenster, WASD-bewegbarer Cube, andere Spieler als Cubes gerendert, Partikel-Regen visualisiert
- **Server**: LiteNetLib-Listener, Session-Management, 20-Hz-Game-Loop, Broadcast, deterministisches Partikel-System als Lasttest
- **Shared**: MemoryPack-Messages, blittable Structs per MemoryMarshal, gemeinsame Physikformel

Der POC verzichtet auf: ECS-Framework, SQLite, Visibility-Culling, Client-Side-Prediction. Alles wird direkt implementiert.

---

### Projekt-Setup

```bash
dotnet new sln -n MmorpgPoc
dotnet new classlib -n Shared -f net10.0
dotnet new console  -n Server -f net10.0
dotnet new console  -n Client -f net10.0
dotnet sln add Shared/Shared.csproj Server/Server.csproj Client/Client.csproj
dotnet add Server reference ../Shared
dotnet add Client reference ../Shared

# NuGet
dotnet add Shared package MemoryPack
dotnet add Client package Silk.NET.OpenGL
dotnet add Client package Silk.NET.Windowing
dotnet add Client package Silk.NET.Input
```

Alle drei `.csproj` brauchen:
```xml
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

`System.Net.Quic` ist Teil der BCL ab .NET 9 — kein NuGet. Erfordert msquic (kommt mit .NET 9+ Runtime auf Windows/Linux).

---

### Verzeichnisstruktur

```
MmorpgPoc/
├── Shared/
│   ├── Protocol.cs       Opcodes, Framing-Konstanten
│   ├── Messages.cs       MemoryPack-Klassen (Input, Events)
│   └── Snapshots.cs      Blittable Structs (Positions, Particles)
├── Server/
│   ├── Program.cs        Einstieg, LiteNetServer starten
│   ├── LiteNetServer.cs  UDP-Listener, Session-Accept via INetEventListener
│   ├── Session.cs        Pro-Verbindung: Channel<CInputState>, NetPeer
│   ├── GameLoop.cs       20-Hz-Tick, Broadcast
│   └── ParticleSystem.cs Deterministisches Partikel-Update
└── Client/
    ├── Program.cs        Einstieg, LiteNetClient verbinden, Window öffnen
    ├── LiteNetClient.cs  UDP Send/Receive, INetEventListener
    ├── InputHandler.cs   WASD + Maus + Jump → CInputState
    ├── Interpolator.cs   Snapshot-Buffer → interpolierte Positionen
    └── Renderer.cs       Silk.NET OpenGL: Cubes + Partikel
```

---

### Shared/Protocol.cs

```csharp
namespace Shared;

public enum Opcode : ushort
{
    // Client → Server (reliable stream)
    CInputState     = 1,

    // Server → Client (reliable stream)
    SPlayerJoined   = 100,
    SPlayerLeft     = 101,

    // Server → Client (QUIC Datagram, unreliable)
    SWorldSnapshot    = 200,   // Spieler-Positionen, gebatcht
    SParticleSnapshot = 201,   // Partikel-Positionen
}

public static class Framing
{
    // Reliable Stream: [Opcode: ushort][Length: uint][Payload: byte[Length]]
    // Datagram:        [Opcode: ushort][Count: ushort][Structs...]

    public const int StreamHeaderSize  = sizeof(ushort) + sizeof(uint);   // 6 Bytes
    public const int DatagramHeaderSize = sizeof(ushort) + sizeof(ushort); // 4 Bytes

    public const float TickRate  = 20f;
    public const float TickDelta = 1f / TickRate;        // 0.05s
    public const float MoveSpeed = 5f;                   // Units pro Sekunde
    public const int   ParticleCount = 1000;             // Lasttest-Partikel
    public const float WorldRadius   = 30f;
}
```

---

### Shared/Messages.cs

MemoryPack-Typen für den reliable Stream:

```csharp
using MemoryPack;

namespace Shared;

[MemoryPackable]
public partial class CInputState
{
    public uint Tick    { get; init; }
    public bool Forward { get; init; }
    public bool Backward{ get; init; }
    public bool Left    { get; init; }
    public bool Right   { get; init; }
}

[MemoryPackable]
public partial class SPlayerJoined
{
    public uint PlayerId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}

[MemoryPackable]
public partial class SPlayerLeft
{
    public uint PlayerId { get; init; }
}

// Envelope für den reliable Stream
[MemoryPackable]
[MemoryPackUnion(100, typeof(SPlayerJoined))]
[MemoryPackUnion(101, typeof(SPlayerLeft))]
public partial abstract class ServerEvent { }
```

---

### Shared/Snapshots.cs

Blittable Structs für Datagrams — kein Serializer, direkt MemoryMarshal:

```csharp
using System.Runtime.InteropServices;

namespace Shared;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerSnapshot  // 16 Bytes
{
    public uint  PlayerId;
    public float X, Y, Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ParticleSnapshot  // 12 Bytes
{
    public float X, Y, Z;
}

// Hilfsklassen für Datagram-Encode/Decode
public static class DatagramWriter
{
    // Schreibt [Opcode: ushort][Count: ushort][Structs...] in einen Span<byte>
    public static int Write<T>(Span<byte> buf, Opcode op, ReadOnlySpan<T> items)
        where T : unmanaged
    {
        int structSize = Marshal.SizeOf<T>();
        BitConverter.TryWriteBytes(buf,       (ushort)op);
        BitConverter.TryWriteBytes(buf[2..],  (ushort)items.Length);
        MemoryMarshal.AsBytes(items).CopyTo(buf[4..]);
        return 4 + items.Length * structSize;
    }

    // Liest Count und gibt Span<T> zurück (zero-copy)
    public static ReadOnlySpan<T> Read<T>(ReadOnlySpan<byte> buf)
        where T : unmanaged
    {
        int count = BitConverter.ToUInt16(buf[2..]);
        return MemoryMarshal.Cast<byte, T>(buf[4..(4 + count * Marshal.SizeOf<T>())]);
    }
}
```

---

### Server/Program.cs

```csharp
using System.Net.Quic;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Server;

// Self-signed Cert für Entwicklung
var rsa  = RSA.Create(2048);
var req  = new CertificateRequest("cn=localhost", rsa,
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

var server    = new QuicServer(cert);
var gameLoop  = new GameLoop(server);

await Task.WhenAll(
    server.RunAsync(),
    gameLoop.RunAsync()
);
```

---

### Server/QuicServer.cs

```csharp
using System.Net.Quic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Shared;

namespace Server;

public class QuicServer
{
    private readonly QuicListener       _listener;
    private readonly List<Session>      _sessions = [];
    private readonly Lock               _sessionsLock = new();
    private uint                        _nextId = 1;

    public QuicServer(X509Certificate2 cert)
    {
        var opts = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Any, 7777),
            ApplicationProtocols = [new SslApplicationProtocol("mmorpg")],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(
                new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode  = 0,
                    ServerAuthenticationOptions = new()
                    {
                        ServerCertificate = cert,
                        ApplicationProtocols = [new SslApplicationProtocol("mmorpg")]
                    }
                })
        };
        _listener = QuicListener.ListenAsync(opts).GetAwaiter().GetResult();
    }

    public IReadOnlyList<Session> Sessions
    {
        get { lock (_sessionsLock) return [.. _sessions]; }
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var conn    = await _listener.AcceptConnectionAsync();
            var session = new Session(conn, _nextId++, this);
            lock (_sessionsLock) _sessions.Add(session);
            _ = session.RunAsync();   // fire-and-forget, Session räumt sich selbst auf
        }
    }

    public void Remove(Session s) { lock (_sessionsLock) _sessions.Remove(s); }
}
```

---

### Server/Session.cs

```csharp
using System.Net.Quic;
using System.Threading.Channels;
using MemoryPack;
using Shared;

namespace Server;

public class Session(QuicConnection conn, uint id, QuicServer server)
{
    public uint  PlayerId => id;
    public float X, Y, Z;                  // vom GameLoop geschrieben (single-threaded Tick)

    private readonly Channel<CInputState> _inputs =
        Channel.CreateBounded<CInputState>(64);

    public ChannelReader<CInputState> Inputs => _inputs.Reader;

    // Liefert alle gepufferten Inputs als Span (nonblocking)
    public CInputState? TryDequeueInput()
    {
        _inputs.Reader.TryRead(out var input);
        return input;
    }

    public async Task RunAsync()
    {
        try
        {
            // Stream 0: bidirektionaler Control-Stream
            var stream = await conn.AcceptInboundStreamAsync();
            await ReceiveLoopAsync(stream);
        }
        finally
        {
            server.Remove(this);
        }
    }

    private async Task ReceiveLoopAsync(QuicStream stream)
    {
        var buf = new byte[4096];
        while (true)
        {
            // Header lesen (6 Bytes: opcode + length)
            await stream.ReadExactlyAsync(buf.AsMemory(0, Framing.StreamHeaderSize));
            var op  = (Opcode)BitConverter.ToUInt16(buf);
            var len = BitConverter.ToUInt32(buf, 2);

            await stream.ReadExactlyAsync(buf.AsMemory(0, (int)len));

            if (op == Opcode.CInputState)
            {
                var input = MemoryPackSerializer.Deserialize<CInputState>(buf.AsSpan(0, (int)len));
                _inputs.Writer.TryWrite(input!);
            }
        }
    }

    // Sendet ein Datagram (fire-and-forget, unreliable)
    public void SendDatagram(ReadOnlyMemory<byte> data) =>
        _ = conn.SendDatagramAsync(data).AsTask();

    // Sendet auf dem reliable Stream (length-prefixed)
    public async Task SendReliableAsync(QuicStream stream, Opcode op, byte[] payload)
    {
        var header = new byte[Framing.StreamHeaderSize];
        BitConverter.TryWriteBytes(header,    (ushort)op);
        BitConverter.TryWriteBytes(header[2..], (uint)payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }
}
```

---

### Server/GameLoop.cs

```csharp
using Shared;
using System.Runtime.InteropServices;

namespace Server;

public class GameLoop(QuicServer server)
{
    private readonly ParticleSystem _particles = new(Framing.ParticleCount);
    private uint _tick;

    public async Task RunAsync()
    {
        var interval = TimeSpan.FromSeconds(Framing.TickDelta);
        while (true)
        {
            var tickStart = DateTime.UtcNow;

            Tick();

            var elapsed = DateTime.UtcNow - tickStart;
            var sleep   = interval - elapsed;
            if (sleep > TimeSpan.Zero)
                await Task.Delay(sleep);
        }
    }

    private void Tick()
    {
        _tick++;
        var sessions = server.Sessions;

        // 1. Input anwenden
        foreach (var s in sessions)
        {
            while (s.TryDequeueInput() is { } input)
                ApplyInput(s, input);
        }

        // 2. Partikel updaten
        _particles.Update(_tick);

        // 3. WorldSnapshot-Datagram bauen und senden
        var playerSnaps = new PlayerSnapshot[sessions.Count];
        for (int i = 0; i < sessions.Count; i++)
            playerSnaps[i] = new() { PlayerId = sessions[i].PlayerId,
                                     X = sessions[i].X,
                                     Y = sessions[i].Y,
                                     Z = sessions[i].Z };

        var worldBuf = new byte[Framing.DatagramHeaderSize +
                                playerSnaps.Length * Marshal.SizeOf<PlayerSnapshot>()];
        DatagramWriter.Write<PlayerSnapshot>(worldBuf, Opcode.SWorldSnapshot, playerSnaps);

        // 4. ParticleSnapshot-Datagram bauen
        var particleBuf = new byte[Framing.DatagramHeaderSize +
                                   _particles.Count * Marshal.SizeOf<ParticleSnapshot>()];
        DatagramWriter.Write<ParticleSnapshot>(particleBuf, Opcode.SParticleSnapshot,
                                               _particles.Positions);

        // 5. Broadcast
        foreach (var s in sessions)
        {
            s.SendDatagram(worldBuf);
            s.SendDatagram(particleBuf);
        }
    }

    private static void ApplyInput(Session s, CInputState input)
    {
        float dx = 0, dz = 0;
        if (input.Forward)  dz += 1;
        if (input.Backward) dz -= 1;
        if (input.Left)     dx -= 1;
        if (input.Right)    dx += 1;

        // Normalisieren bei Diagonale
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len > 0) { dx /= len; dz /= len; }

        s.X += dx * Framing.MoveSpeed * Framing.TickDelta;
        s.Z += dz * Framing.MoveSpeed * Framing.TickDelta;
    }
}
```

---

### Server/ParticleSystem.cs

Deterministisch: Client kann dieselbe Formel lokal ausführen und vorher-sagen. Im POC sendet der Server trotzdem alle Positionen — das ist der Lasttest.

```csharp
using Shared;

namespace Server;

public class ParticleSystem(int count)
{
    private readonly ParticleSnapshot[] _positions = new ParticleSnapshot[count];
    public int Count => count;
    public ReadOnlySpan<ParticleSnapshot> Positions => _positions;

    public void Update(uint tick)
    {
        float t = tick * Framing.TickDelta;
        for (int i = 0; i < count; i++)
        {
            float phase = i * 0.1f;
            _positions[i] = new()
            {
                X = MathF.Sin(t * 0.3f + phase) * Framing.WorldRadius,
                Y = 20f - (t * 2f + i * 0.3f) % 20f,   // fällt von Y=20 auf Y=0
                Z = MathF.Cos(t * 0.3f + phase) * Framing.WorldRadius,
            };
        }
    }
}
```

---

### Client/Program.cs

```csharp
using System.Net.Quic;
using System.Net;
using System.Net.Security;
using Client;

var connOpts = new QuicClientConnectionOptions
{
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7777),
    DefaultStreamErrorCode = 0,
    DefaultCloseErrorCode  = 0,
    ClientAuthenticationOptions = new SslClientAuthenticationOptions
    {
        ApplicationProtocols = [new SslApplicationProtocol("mmorpg")],
        RemoteCertificateValidationCallback = (_, _, _, _) => true  // dev only
    }
};

var conn        = await QuicConnection.ConnectAsync(connOpts);
var quicClient  = new QuicClient(conn);
var interpolator = new Interpolator();
var renderer    = new Renderer(interpolator);

await Task.WhenAll(
    quicClient.RunAsync(interpolator),
    renderer.RunAsync(quicClient)     // öffnet Fenster, blockiert bis Close
);
```

---

### Client/QuicClient.cs

```csharp
using System.Net.Quic;
using System.Threading.Channels;
using MemoryPack;
using Shared;

namespace Client;

public class QuicClient(QuicConnection conn)
{
    private QuicStream? _controlStream;
    private readonly Channel<CInputState> _outgoing =
        Channel.CreateUnbounded<CInputState>();

    public void EnqueueInput(CInputState input) => _outgoing.Writer.TryWrite(input);

    public async Task RunAsync(Interpolator interpolator)
    {
        _controlStream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);

        await Task.WhenAll(
            SendLoopAsync(),
            DatagramLoopAsync(interpolator)
        );
    }

    private async Task SendLoopAsync()
    {
        await foreach (var input in _outgoing.Reader.ReadAllAsync())
        {
            var payload = MemoryPackSerializer.Serialize(input);
            var header  = new byte[Framing.StreamHeaderSize];
            BitConverter.TryWriteBytes(header,     (ushort)Opcode.CInputState);
            BitConverter.TryWriteBytes(header[2..], (uint)payload.Length);
            await _controlStream!.WriteAsync(header);
            await _controlStream!.WriteAsync(payload);
        }
    }

    private async Task DatagramLoopAsync(Interpolator interp)
    {
        while (true)
        {
            var dg  = await conn.ReceiveDatagramAsync();
            var op  = (Opcode)BitConverter.ToUInt16(dg.Span);
            switch (op)
            {
                case Opcode.SWorldSnapshot:
                    interp.UpdatePlayers(DatagramWriter.Read<PlayerSnapshot>(dg.Span));
                    break;
                case Opcode.SParticleSnapshot:
                    interp.UpdateParticles(DatagramWriter.Read<ParticleSnapshot>(dg.Span));
                    break;
            }
        }
    }
}
```

---

### Client/Interpolator.cs

```csharp
using Shared;

namespace Client;

// Thread-safe Snapshot-Store: Netzwerk-Thread schreibt, Render-Thread liest
public class Interpolator
{
    private volatile PlayerSnapshot[]   _players   = [];
    private volatile ParticleSnapshot[] _particles = [];

    public void UpdatePlayers(ReadOnlySpan<PlayerSnapshot> snaps) =>
        _players = snaps.ToArray();

    public void UpdateParticles(ReadOnlySpan<ParticleSnapshot> snaps) =>
        _particles = snaps.ToArray();

    public PlayerSnapshot[]   Players   => _players;
    public ParticleSnapshot[] Particles => _particles;
}
```

---

### Client/InputHandler.cs

```csharp
using Silk.NET.Input;
using Shared;

namespace Client;

public class InputHandler(IInputContext ctx, QuicClient client, uint localPlayerId)
{
    private uint _tick;

    // Wird vom Render-Loop (OnUpdate) aufgerufen
    public void Poll()
    {
        var kb = ctx.Keyboards[0];
        var input = new CInputState
        {
            Tick     = _tick++,
            Forward  = kb.IsKeyPressed(Key.W),
            Backward = kb.IsKeyPressed(Key.S),
            Left     = kb.IsKeyPressed(Key.A),
            Right    = kb.IsKeyPressed(Key.D),
        };
        client.EnqueueInput(input);
    }
}
```

---

### Client/Renderer.cs

OpenGL 3.3 Core Profile via Silk.NET. Kein Texture-Loading, nur Flat-Shading.

**Shaders:**

```csharp
// Vertex Shader (als C#-String-Literal)
const string VertSrc = """
    #version 330 core
    layout(location = 0) in vec3 aPos;
    uniform mat4 uMVP;
    void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
    """;

// Fragment Shader
const string FragSrc = """
    #version 330 core
    uniform vec3 uColor;
    out vec4 FragColor;
    void main() { FragColor = vec4(uColor, 1.0); }
    """;
```

**Cube-Geometry:**

```csharp
float[] CubeVerts = [
    -0.5f,-0.5f,-0.5f,  0.5f,-0.5f,-0.5f,  0.5f, 0.5f,-0.5f, -0.5f, 0.5f,-0.5f,
    -0.5f,-0.5f, 0.5f,  0.5f,-0.5f, 0.5f,  0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f,
];

uint[] CubeIdx = [
    0,1,2, 2,3,0,   // hinten
    4,5,6, 6,7,4,   // vorne
    0,4,7, 7,3,0,   // links
    1,5,6, 6,2,1,   // rechts
    0,1,5, 5,4,0,   // unten
    3,2,6, 6,7,3,   // oben
];
```

**Kamera (View + Projection):**

```csharp
// Feste isometrische Kamera, kein Tracking nötig für POC
Matrix4x4 view       = Matrix4x4.CreateLookAt(new(0,25,-25), Vector3.Zero, Vector3.UnitY);
Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                           MathF.PI / 3f, width / (float)height, 0.1f, 500f);
Matrix4x4 vp         = view * projection;
```

**Render-Loop (OnRender):**

```csharp
// Pro Spieler: Cube in PlayerColor(id) an Position
foreach (var p in interpolator.Players)
{
    var model = Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
    gl.UniformMatrix4(mvpLoc, 1, false, (model * vp).ToFloatArray());
    gl.Uniform3(colorLoc, PlayerColor(p.PlayerId));
    gl.DrawElements(GLEnum.Triangles, 36, GLEnum.UnsignedInt, null);
}

// Partikel: kleiner Cube (Scale 0.1) oder gl_Points
gl.PointSize(4f);
// alternativ: Cube mit Scale(0.1f) rendern
foreach (var p in interpolator.Particles)
{
    var model = Matrix4x4.CreateScale(0.15f) * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
    gl.UniformMatrix4(mvpLoc, 1, false, (model * vp).ToFloatArray());
    gl.Uniform3(colorLoc, 0.3f, 0.6f, 1.0f);   // hellblau für Regen
    gl.DrawElements(GLEnum.Triangles, 36, GLEnum.UnsignedInt, null);
}
```

**PlayerColor(id):** `id` als Seed für deterministisches HSV → RGB. Jeder Spieler hat einen eindeutigen Farbton.

---

### Implementierungsreihenfolge für den Agent

Exakt in dieser Reihenfolge implementieren — jeder Schritt ist testbar bevor der nächste beginnt:

1. **Shared-Projekt**: `Protocol.cs`, `Messages.cs`, `Snapshots.cs` — compiliert, keine Runtime nötig
2. **Server/Program.cs + QuicServer.cs** — `dotnet run` im Server-Projekt, muss auf Port 7777 lauschen
3. **Server/Session.cs** — Verbindung akzeptieren, Inputs empfangen (mit `nc` oder einfachem Test-Client verifizieren)
4. **Server/ParticleSystem.cs** — Unit-Test: 3 Ticks simulieren, Positionen ausgeben
5. **Server/GameLoop.cs** — Server schreibt Tick-Logs auf Console: `Tick 1: 0 players, 1000 particles`
6. **Client/QuicClient.cs** — Verbindung aufbauen, Control-Stream öffnen. Server-Log zeigt Session.
7. **Client/InputHandler.cs** — WASD → EnqueueInput. Server-Log zeigt eingehende Inputs.
8. **Client/Interpolator.cs** — Snapshots empfangen, `Players.Length` auf Console ausgeben
9. **Client/Renderer.cs** — Silk.NET-Fenster mit leerem Cube (keine Netzwerk-Daten), Kamera OK
10. **Client/Program.cs** — Alles zusammenstecken. Cube bewegt sich bei WASD, andere Spieler sichtbar, Partikel-Regen fällt

---

### Lasttest-Parameter

```csharp
// Server/Program.cs: Partikelzahl via Argument
int particleCount = args.Length > 0 ? int.Parse(args[0]) : 1000;
var gameLoop = new GameLoop(server, particleCount);
```

Benchmark-Progression:

| Partikel | Datagram-Größe/Tick | Erwartetes Ergebnis |
|----------|--------------------|--------------------|
| 100 | ~1.2 KB | Baseline, alles stabil |
| 1 000 | ~12 KB | Sichtbarer Regen, < 1ms Broadcast |
| 5 000 | ~60 KB | Grenzbereich, Framerate-Impact messen |
| 10 000 | ~120 KB | QUIC-Datagram-Limit prüfen (ggf. auf Stream wechseln) |

Hinweis: QUIC-Datagrams haben eine PMTU-abhängige Größenlimitierung (~1200–8000 Bytes je nach Netzpfad). Ab ~1000 Partikeln (12 KB) muss das Datagram entweder auf einen **reliable Stream** umgestellt oder in Chunks aufgeteilt werden. Im POC: bei > 1000 Partikeln automatisch auf Stream wechseln.

---

### Smoke-Test / Definition of Done

- [ ] Server startet, loggt `Listening on :7777`
- [ ] Client startet, Fenster öffnet sich (800×600, Titel "MMORPG POC")
- [ ] Client verbindet, Server loggt `Session 1 connected`
- [ ] WASD bewegt den Cube auf dem Bildschirm
- [ ] Zweiter Client startet: beide sehen sich gegenseitig als Cube
- [ ] Partikel-Regen fällt von Y=20 auf Y=0 und reset sich
- [ ] `dotnet run --project Server 5000` → 5000 Partikel, Client rendert ohne Absturz
- [ ] Server-Console zeigt Tick-Timing: `Tick 100: 2ms` (deutlich unter 50ms Budget)

---

## Was dieses Dokument bewusst auslässt

- **Matchmaking / Login-Server**: Standard-Webservices, kein MMO-spezifisches Problem
- **Billing / Shop**: Außerhalb des Spielkern
- **DevOps / Deployment**: Kubernetes, Observability — eigenständiges Thema
- **Konkrete Technologie-Stacks**: Sprache, Engine — abhängig von Team und Kontext
