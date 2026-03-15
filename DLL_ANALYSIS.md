# Assembly-CSharp.dll 解析メモ（プレイヤー位置まわり）

Mono.Cecil で `D:\Steam\steamapps\common\MIMESIS\MIMESIS_Data\Managed\Assembly-CSharp.dll` を解析した結果の要約です。

## プレイヤー位置（Vector3）の候補

### 1. Mimic.Actors.ProtoActor（本ゲームのアクター）

- **oldPos** : `System.Nullable<UnityEngine.Vector3>`
- **syncTargetPosVForAnimation** : `System.Nullable<UnityEngine.Vector3>`
- **lastCharacterControlerHorizontalVelocity** : `UnityEngine.Vector3`
- **debug_oldMoveCurr** : `System.Nullable<UnityEngine.Vector3>`
- MonoBehaviour 継承と想定され、`transform.position` で現在位置を取得可能。

→ MOD では `FindObjectOfType(ProtoActor)` で取得し、`transform` から位置・向きを参照。

### 2. その他 Mimic 名前空間の Vector3

- **Mimic.Audio.IndoorOutdoorDetector** — `lastHeadPosition` (Vector3)
- **Mimic.Actors.MoveHint** — `Position` (Vector3)
- **Mimic.Actors.ProjectileActor** — `InitialPosition`, `InitialForward` など

### 3. デモ・アセット由来の Player 系

- **BuildingMakerToolset.Demo.PlayerMovement** — `lastPosition`, `targetVelocity` (Vector3)、`controller` (CharacterController)
- **SimpleFPSController.FirstPersonController** — `m_MoveDir` (Vector3)、`m_CharacterController`
- **FS_OfficePack.SimplePlayerController** — `moveDirection` (Vector3)
- **DunGen.Demo.PlayerController** — `velocity` (Vector3)、`movementController`

本編プレイヤーは **Mimic.Actors.ProtoActor** を前提にし、フォールバックで Tag "Player" と CharacterController を使用しています。

---

## マップ・ダンジョン情報（DunGen / ミニマップ用）

MIMESIS は **DunGen**（Unity アセット）でプロシージャルダンジョンを生成しています。Assembly-CSharp 内に DunGen の型が含まれており、**ミニマップは「プレイヤー周辺の Physics スキャン」ではなく、このダンジョン構造を直接参照すると正確な部屋配置が得られます。**

### 取得の流れ（推奨）

1. **DunGen.DungeonGenerator**  
   - シーンに1つ存在する想定。`FindObjectOfType(DungeonGenerator)` で取得。
   - プロパティ **CurrentDungeon** (DunGen.Dungeon) で現在生成済みのダンジョンへアクセス。

2. **DunGen.Dungeon**
   - **Bounds** … ダンジョン全体の AABB（UnityEngine.Bounds）。
   - **AllTiles** / **allTiles** … 全ルーム（タイル）のリスト `List<DunGen.Tile>`。
   - **connections** … ドアウェイ接続（隣接関係の把握に利用可能）。

3. **DunGen.Tile**（＝1ルーム）
   - **placement** (DunGen.TilePlacementData) を参照。
   - **placement.worldBounds** … そのルームのワールド空間での Bounds（ミニマップの「部屋の四角」にそのまま使える）。
   - **placement.position** … 配置位置（Vector3）。
   - **transform** … MonoBehaviour 継承のため、タイル実体の Transform も利用可能。

4. **DunGen.AdjacentRoomCulling**（オプション）
   - ゲーム側でカリング用に利用している可能性あり（relumod_ プレフィックス付きメンバあり）。
   - **generator** (DungeonGenerator)、**dungeon** (Dungeon)、**allTiles** / **visibleTiles**、**currentTile**（プレイヤーがいるタイル）を保持。
   - **relumod_FastFindCurrentTile(Transform, float)** でプレイヤー所在タイルを取得可能。
   - **relumod_tilesByXZ** … XZ で整理されたタイルリスト（`List<List<List<Tile>>>`）。

### ミニマップ実装のポイント

- **DungeonGenerator.CurrentDungeon** が null でないときだけ、DunGen ベースの描画に切り替える。
- 各 **Tile** の **placement.worldBounds** を XZ 平面に投影し、矩形として描画（床＝塗りつぶし、壁＝枠線など）。
- プレイヤー位置は従来どおり **Mimic.Actors.ProtoActor** 等で取得。現在タイルは **AdjacentRoomCulling.currentTile** や **relumod_FastFindCurrentTile** で取得すると、部屋単位のハイライトなどに使える。
- DunGen が使われていないシーン（CurrentDungeon == null）では、従来の「プレイヤー中心の occupancy grid / OverlapSphere」にフォールバックする。
