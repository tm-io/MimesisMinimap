using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(MimesisMinimap.MimesisMinimapMod), "MimesisMinimap", "0.2.0", "Author")]
[assembly: MelonGame(null, null)] // ゲーム名は未指定で汎用

namespace MimesisMinimap
{
    /// <summary>
    /// MIMESIS用 MelonLoader ミニマップMOD（RenderTexture方式）。
    /// プレイヤー頭上から真下を見下ろす Orthographic Camera の映像を RenderTexture で RawImage に表示。
    /// ローカルプレイヤー: Camera.main の位置／追従先、または Mimic.Actors.ProtoActor の IsOwner で判定（FishNet マルチ対応）。
    /// </summary>
    public class MimesisMinimapMod : MelonMod
    {
        private Transform _playerTransform;
        private bool _showMinimap = true;

        /// <summary>ミニマップUIのサイズ（ピクセル）</summary>
        private const float MinimapSize = 200f;
        /// <summary>ミニマップカメラをプレイヤー頭上に配置する高さオフセット（Y + この値）</summary>
        private const float MinimapCameraHeightOffset = 50f;
        /// <summary>RenderTexture 解像度</summary>
        private const int MinimapRenderTextureSize = 256;

        // ランタイム生成した UI / カメラ
        private Canvas _minimapCanvas;
        private RawImage _minimapRawImage;
        private RectTransform _minimapRectTransform;
        /// <summary>ミニマップ上に表示するプレイヤー位置・向き用アイコン（RawImage の子）。</summary>
        private RectTransform _playerIconRect;
        /// <summary>レーダー用三角アイコンのスプライト（味方・敵で色だけ変える）。</summary>
        private Sprite _triangleIconSprite;
        /// <summary>レーダーアイコン用コンテナ（RawImage の子）。</summary>
        private RectTransform _radarIconContainer;
        /// <summary>レーダーアイコンのプール。味方・敵の三角マーカーを再利用。</summary>
        private List<RadarIconEntry> _radarIconPool;
        /// <summary>宝物・アイテム用丸アイコンのスプライト（黄色で表示）。</summary>
        private Sprite _circleIconSprite;
        /// <summary>アイテムアイコン用コンテナ（RawImage の子）。</summary>
        private RectTransform _itemIconContainer;
        /// <summary>アイテムアイコンのプール。</summary>
        private List<RadarIconEntry> _itemIconPool;
        /// <summary>Value/Price 等を持つアイテム型をキャッシュ（初期化時にアセンブリ走査）。O(1) 検索のため HashSet。</summary>
        private static HashSet<Type> _itemTypes;
        /// <summary>アイテムの Transform キャッシュ。重複なし・O(1)削除のため HashSet。</summary>
        public static HashSet<Transform> CachedItemTransforms = new HashSet<Transform>();
        private float _itemScanTimer = 0f;
        private const float ItemScanInterval = 2.0f;
        private GameObject _minimapCameraGo;
        private Camera _minimapCamera;
        private RenderTexture _minimapRenderTexture;
        /// <summary>ミニマップ描画時のみ点灯する Directional Light（ダンジョン内の環境光不足対策）。</summary>
        private Light _minimapLight;
        /// <summary>現在のズーム倍率（Orthographic Size）。</summary>
        private float _currentOrthoSize = 25f;

        private static MelonPreferences_Category _configCategory;
        private static MelonPreferences_Entry<bool> _cfgShowEnemies;
        private static MelonPreferences_Entry<bool> _cfgShowAllies;
        private static MelonPreferences_Entry<bool> _cfgShowItems;
        private static MelonPreferences_Entry<float> _cfgZoomStep;

        // DunGen（Assembly-CSharp）からマップ情報を取得するためのリフレクションキャッシュ（Harmony／将来の拡張用に保持）
        private static Assembly _gameAssembly;
        private static Type _dungeonGeneratorType;
        private static Type _dungeonType;
        private static Type _tileType;
        private static Type _tilePlacementDataType;
        private static Type _runtimeDungeonType;
        private static Type _adjacentRoomCullingType;
        private static PropertyInfo _currentDungeonProp;
        private static FieldInfo _dungeonAllTilesField;
        private static FieldInfo _tilePlacementField;
        private static FieldInfo _placementWorldBoundsField;
        private static FieldInfo _placementLocalBoundsField;
        private static FieldInfo _placementPositionField;
        private static FieldInfo _placementRotationField;
        private static FieldInfo _generatorRootField;
        private static FieldInfo _runtimeDungeonRootField;
        private static MethodInfo _unityUtilTransformBoundsMethod;
        private static FieldInfo _runtimeDungeonGeneratorField;
        private static FieldInfo _adjacentCullingDungeonField;
        private static FieldInfo _adjacentCullingGeneratorField;
        private static FieldInfo _adjacentCullingAllTilesField;
        private static FieldInfo _adjacentCullingAllDoorsField;
        private static FieldInfo _dungeonConnectionsField;
        private static FieldInfo _dungeonAttachmentTileField;
        private static Type _doorType;
        private static FieldInfo _doorDoorwayAField;
        private static FieldInfo _doorDoorwayBField;
        private static Type _doorwayConnectionType;
        private static PropertyInfo _connAProp;
        private static PropertyInfo _connBProp;
        private static FieldInfo _connAField;
        private static FieldInfo _connBField;
        private static Type _doorwayType;
        private static PropertyInfo _doorwayTransformProp;
        private static PropertyInfo _generatorAttachmentSettingsProp;
        private static Type _attachmentSettingsType;
        private static PropertyInfo _attachmentDoorwayProp;
        private static MethodInfo _tileGetExitDoorwayMethod;
        private static MethodInfo _tileGetEntranceDoorwayMethod;
        private static FieldInfo _doorwayConnectedDoorwayField;
        private static bool _dunGenReflectionResolved;
        private static bool _dunGenReflectionFailed;
        private DunGenMapData _cachedDungeonMapData;
        private static string _dunGenNegativeCacheSceneName;
        private static int _lastDunGenTryFrame = -1;
        private const int DunGenRetryIntervalFrames = 60;
        private static bool _harmonyHooksApplied;

        /// <summary>ミニマップも DunGen 探索も不要なシーン。</summary>
        private static readonly string[] ScenesWithoutMinimap = { "LoginScene", "MainMenuScene", "MaintenanceScene", "InTramWaitingScene" };

        /// <summary>レーダーアイコン1つ分の UI 参照（プール用）。</summary>
        private sealed class RadarIconEntry
        {
            public RectTransform Rect;
            public Image Image;
        }

        private static bool IsMinimapNeededForScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            for (int i = 0; i < ScenesWithoutMinimap.Length; i++)
                if (string.Equals(ScenesWithoutMinimap[i], sceneName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public override void OnInitializeMelon()
        {
            _configCategory = MelonPreferences.CreateCategory("MimesisMinimap");
            _cfgShowEnemies = _configCategory.CreateEntry("ShowEnemies", true, "Show Enemies (Red Triangle)");
            _cfgShowAllies = _configCategory.CreateEntry("ShowAllies", true, "Show Allies (Green Triangle)");
            _cfgShowItems = _configCategory.CreateEntry("ShowItems", true, "Show Items (Yellow Circle)");
            _cfgZoomStep = _configCategory.CreateEntry("ZoomStep", 5f, "Zoom step size");

            MelonLogger.Msg("MimesisMinimap: 初期化しました（RenderTexture方式）。");
            ApplyEntranceAndDoorHooks();
            RegisterMinimapLightCallbacks();
            ResolveItemTypes();
        }

        public override void OnDeinitializeMelon()
        {
            UnregisterMinimapLightCallbacks();
        }

        private void ApplyEntranceAndDoorHooks()
        {
            if (_harmonyHooksApplied) return;
            try
            {
                Type harmonyType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        harmonyType = asm.GetType("HarmonyLib.Harmony");
                        if (harmonyType == null) harmonyType = asm.GetType("Harmony.HarmonyInstance");
                        if (harmonyType != null) break;
                    }
                    catch (Exception) { }
                }
                if (harmonyType == null) return;
                object harmonyInstance = Activator.CreateInstance(harmonyType, "MimesisMinimap");
                if (harmonyInstance == null) return;
                Type harmonyMethodType = harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod") ?? harmonyType.Assembly.GetType("Harmony.HarmonyMethod");
                if (harmonyMethodType == null) return;
                MethodInfo patchMethod = null;
                foreach (var m in harmonyType.GetMethods())
                {
                    var ps = m.GetParameters();
                    if (m.Name == "Patch" && ps.Length >= 2 && ps[0].ParameterType == typeof(MethodBase) && ps[1].ParameterType == harmonyMethodType) { patchMethod = m; break; }
                }
                if (patchMethod == null) return;
                var patchArgs = new object[] { null, null, null, null, null };
                MethodBase loadSceneStr = typeof(SceneManager).GetMethod("LoadScene", new[] { typeof(string) });
                if (loadSceneStr != null)
                {
                    var prefix = typeof(EntranceDoorHookTargets).GetMethod("LoadSceneStringPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    if (prefix != null)
                    {
                        object prefixHarmony = Activator.CreateInstance(harmonyMethodType, prefix);
                        patchArgs[0] = loadSceneStr; patchArgs[1] = prefixHarmony;
                        patchMethod.Invoke(harmonyInstance, patchArgs);
                        MelonLogger.Msg("MimesisMinimap: シーン読み込みをフックしました（出入り口通過時のログ用）。");
                    }
                }
                ResolveDunGenReflection();
                if (_gameAssembly != null)
                {
                    Type doorType = _gameAssembly.GetType("DunGen.Door");
                    if (doorType != null)
                    {
                        MethodBase setDoorState = doorType.GetMethod("SetDoorState", new[] { typeof(bool) });
                        if (setDoorState != null)
                        {
                            var prefix = typeof(EntranceDoorHookTargets).GetMethod("SetDoorStatePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                            if (prefix != null)
                            {
                                object prefixHarmony = Activator.CreateInstance(harmonyMethodType, prefix);
                                patchArgs[0] = setDoorState; patchArgs[1] = prefixHarmony; patchArgs[2] = patchArgs[3] = patchArgs[4] = null;
                                patchMethod.Invoke(harmonyInstance, patchArgs);
                                MelonLogger.Msg("MimesisMinimap: ドア開閉をフックしました。");
                            }
                        }
                    }
                }
                _harmonyHooksApplied = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"MimesisMinimap: Harmony フックはスキップしました（{ex.Message}）。");
            }
        }

        /// <summary>アイテム型セットを返す（RefreshItemCache 等で利用）。</summary>
        public static HashSet<Type> GetItemTypes()
        {
            ResolveItemTypes();
            return _itemTypes ?? new HashSet<Type>();
        }

        /// <summary>ドア・自販機等、プレイヤー・敵・キャラクターをアイテムキャッシュから除外するかどうか。</summary>
        public static bool ShouldExcludeFromItemCache(Component comp)
        {
            if (comp == null) return true;
            GameObject go = comp.gameObject;
            if (go.CompareTag("Player")) return true;
            if (go.CompareTag("Enemy")) return true;
            if (go.transform.root.CompareTag("Player")) return true;
            if (go.transform.root.CompareTag("Enemy")) return true;
            string className = comp.GetType().Name ?? "";
            if (className.IndexOf("Actor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            string name = go.name ?? "";
            if (name.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Vending", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            Assembly gameAsm = _gameAssembly;
            if (gameAsm == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { if (asm.GetName().Name == "Assembly-CSharp") { gameAsm = asm; break; } }
                    catch (Exception) { }
                }
            }
            Type protoActorType = gameAsm?.GetType("Mimic.Actors.ProtoActor");
            if (protoActorType != null && comp.GetComponentInParent(protoActorType) != null) return true;
            Type doorType = gameAsm?.GetType("DunGen.Door");
            Type doorwayType = gameAsm?.GetType("DunGen.Doorway");
            if (doorType != null && comp.GetComponent(doorType) != null) return true;
            if (doorwayType != null && comp.GetComponent(doorwayType) != null) return true;
            return false;
        }

        private static class EntranceDoorHookTargets
        {
            private static void LoadSceneStringPrefix(string sceneName)
            {
                MelonLogger.Msg($"[MimesisMinimap Hook] LoadScene が呼ばれました: sceneName=\"{sceneName}\"（出入り口通過の可能性）。");
            }
            private static void SetDoorStatePrefix(bool isOpen)
            {
                MelonLogger.Msg($"[MimesisMinimap Hook] SetDoorState(isOpen={isOpen}) が呼ばれました。");
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _playerTransform = null;
            _cachedDungeonMapData = null;
            _dunGenNegativeCacheSceneName = null;
            _lastDunGenTryFrame = -1;
            CachedItemTransforms.Clear();
            DestroyMinimapUI();
            MelonLogger.Msg($"MimesisMinimap: シーン読み込み [{sceneName}] — プレイヤーを再検索します。");
        }

        public override void OnUpdate()
        {
            if (_playerTransform == null)
                ResolvePlayerTransform();

            if (!IsMinimapNeededForScene(SceneManager.GetActiveScene().name))
                return;

            if (!_showMinimap)
            {
                if (_minimapCanvas != null && _minimapCanvas.gameObject.activeSelf)
                    _minimapCanvas.gameObject.SetActive(false);
                return;
            }

            if (_minimapCanvas == null || _minimapRawImage == null)
                InitializeMinimapUI();

            if (_minimapCanvas != null && !_minimapCanvas.gameObject.activeSelf)
                _minimapCanvas.gameObject.SetActive(true);

            // ミニマップカメラをローカルプレイヤー頭上に追従
            UpdateMinimapCameraPosition();
            // キーボードが接続されていない場合は処理しない
            if (Keyboard.current != null)
            {
                // 縮小処理（マイナス または テンキーのマイナス）
                if (Keyboard.current.minusKey.wasPressedThisFrame || Keyboard.current.numpadMinusKey.wasPressedThisFrame)
                {
                    _currentOrthoSize += _cfgZoomStep.Value;
                }

                // 拡大処理（プラス(US配列のEquals)、テンキーのプラス、または Shift + セミコロン(JIS配列)）
                bool isShiftPressed = Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed;
                if (Keyboard.current.equalsKey.wasPressedThisFrame ||
                    Keyboard.current.numpadPlusKey.wasPressedThisFrame ||
                    (isShiftPressed && Keyboard.current.semicolonKey.wasPressedThisFrame))
                {
                    _currentOrthoSize -= _cfgZoomStep.Value;
                }

                _currentOrthoSize = Mathf.Clamp(_currentOrthoSize, 5f, 100f);
                if (_minimapCamera != null)
                {
                    _minimapCamera.orthographicSize = _currentOrthoSize;
                }
            }
            _itemScanTimer += Time.deltaTime;
            if (_itemScanTimer >= ItemScanInterval)
            {
                _itemScanTimer = 0f;
                RefreshItemCache();
            }
            UpdateMinimapIcons();
        }

        /// <summary>
        /// Unity標準UI（Canvas, RawImage, RectTransform）をランタイムで生成し、画面右上にミニマップ枠を構築する。
        /// </summary>
        private void InitializeMinimapUI()
        {
            if (_minimapCanvas != null) return;

            try
            {
                // Canvas (Screen Space - Overlay)
                var canvasGo = new GameObject("MimesisMinimap_Canvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);
                _minimapCanvas = canvasGo.AddComponent<Canvas>();
                _minimapCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _minimapCanvas.pixelPerfect = false;
                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                canvasGo.AddComponent<GraphicRaycaster>();

                // ミニマップ用パネル（枠）
                var panelGo = new GameObject("MinimapPanel");
                panelGo.transform.SetParent(canvasGo.transform, false);
                var panelRect = panelGo.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(1f, 1f);
                panelRect.anchorMax = new Vector2(1f, 1f);
                panelRect.pivot = new Vector2(1f, 1f);
                panelRect.anchoredPosition = new Vector2(-20f, -20f);
                panelRect.sizeDelta = new Vector2(MinimapSize, MinimapSize);
                var panelImage = panelGo.AddComponent<Image>();
                panelImage.color = new Color(0.1f, 0.1f, 0.2f, 0.85f);

                // RawImage（RenderTexture を表示）
                var rawGo = new GameObject("MinimapRawImage");
                rawGo.transform.SetParent(panelGo.transform, false);
                _minimapRectTransform = rawGo.AddComponent<RectTransform>();
                _minimapRectTransform.anchorMin = Vector2.zero;
                _minimapRectTransform.anchorMax = Vector2.one;
                _minimapRectTransform.offsetMin = new Vector2(4f, 4f);
                _minimapRectTransform.offsetMax = new Vector2(-4f, -4f);
                _minimapRawImage = rawGo.AddComponent<RawImage>();
                _minimapRawImage.color = Color.white;

                // ミニマップ専用 Orthographic Camera と RenderTexture
                CreateMinimapCameraAndRenderTexture();
                if (_minimapRenderTexture != null && _minimapRawImage != null)
                    _minimapRawImage.texture = _minimapRenderTexture;

                // レーダー用三角アイコン（味方・敵・自分）のテクスチャとスプライト（PlayerIcon より先に生成）
                _triangleIconSprite = CreateTriangleIconSprite();
                _circleIconSprite = CreateCircleIconSprite();
                _radarIconPool = new List<RadarIconEntry>();
                var radarContainerGo = new GameObject("RadarIconContainer");
                radarContainerGo.transform.SetParent(rawGo.transform, false);
                _radarIconContainer = radarContainerGo.AddComponent<RectTransform>();
                _radarIconContainer.anchorMin = Vector2.zero;
                _radarIconContainer.anchorMax = Vector2.one;
                _radarIconContainer.offsetMin = Vector2.zero;
                _radarIconContainer.offsetMax = Vector2.zero;

                _itemIconPool = new List<RadarIconEntry>();
                var itemContainerGo = new GameObject("ItemIconContainer");
                itemContainerGo.transform.SetParent(rawGo.transform, false);
                _itemIconContainer = itemContainerGo.AddComponent<RectTransform>();
                _itemIconContainer.anchorMin = Vector2.zero;
                _itemIconContainer.anchorMax = Vector2.one;
                _itemIconContainer.offsetMin = Vector2.zero;
                _itemIconContainer.offsetMax = Vector2.zero;

                // プレイヤー位置・向きアイコン（緑三角、レーダーと同じスプライト）
                var playerIconGo = new GameObject("PlayerIcon");
                playerIconGo.transform.SetParent(rawGo.transform, false);
                _playerIconRect = playerIconGo.AddComponent<RectTransform>();
                _playerIconRect.anchorMin = new Vector2(0.5f, 0.5f);
                _playerIconRect.anchorMax = new Vector2(0.5f, 0.5f);
                _playerIconRect.pivot = new Vector2(0.5f, 0.5f);
                _playerIconRect.sizeDelta = new Vector2(12f, 12f);
                _playerIconRect.anchoredPosition = Vector2.zero;
                var playerIconImage = playerIconGo.AddComponent<Image>();
                playerIconImage.sprite = _triangleIconSprite;
                playerIconImage.color = new Color(0.2f, 0.9f, 0.3f, 1f); // 味方と同じ緑
                playerIconImage.raycastTarget = false;

                MelonLogger.Msg("MimesisMinimap: ミニマップUI（Canvas / RawImage / PlayerIcon / レーダー / アイテム）を初期化しました。");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"MimesisMinimap: ミニマップUIの初期化に失敗しました: {ex.Message}");
            }
        }

        /// <summary>
        /// プレイヤー頭上から真下を見下ろす Orthographic Camera をランタイムで生成し、RenderTexture に出力する。
        /// nearClip でプレイヤー約3m上空より下のみ描画し天井をスライス。専用ライトでダンジョン内の環境光不足を補う。
        /// </summary>
        private void CreateMinimapCameraAndRenderTexture()
        {
            if (_minimapCameraGo != null) return;

            _minimapRenderTexture = new RenderTexture(MinimapRenderTextureSize, MinimapRenderTextureSize, 16, RenderTextureFormat.ARGB32);
            _minimapRenderTexture.name = "MimesisMinimap_RT";
            _minimapRenderTexture.Create();

            _minimapCameraGo = new GameObject("MimesisMinimap_Camera");
            UnityEngine.Object.DontDestroyOnLoad(_minimapCameraGo);
            _minimapCamera = _minimapCameraGo.AddComponent<Camera>();
            _minimapCamera.orthographic = true;
            _minimapCamera.orthographicSize = _currentOrthoSize;
            // プレイヤー約3m上空より下だけ描画し、天井メッシュをクリップ（透過）する
            _minimapCamera.nearClipPlane = MinimapCameraHeightOffset - 3f;
            _minimapCamera.farClipPlane = MinimapCameraHeightOffset + 30f;
            _minimapCamera.targetTexture = _minimapRenderTexture;
            _minimapCamera.enabled = true;
            _minimapCamera.clearFlags = CameraClearFlags.SolidColor;
            _minimapCamera.backgroundColor = new Color(0.12f, 0.12f, 0.16f, 1f);

            // 真下向き（Y軸負方向）
            _minimapCameraGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _minimapCamera.cullingMask = -1;
            _minimapCamera.depth = -100;

            // ミニマップ専用 Directional Light（ダンジョン内環境光なし対策）。描画時のみ begin/end で点灯する
            var lightGo = new GameObject("MimesisMinimap_Light");
            lightGo.transform.SetParent(_minimapCameraGo.transform, false);
            lightGo.transform.localPosition = Vector3.zero;
            lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // 真下向き
            _minimapLight = lightGo.AddComponent<Light>();
            _minimapLight.type = LightType.Directional;
            _minimapLight.intensity = 1f;
            _minimapLight.enabled = false;
        }

        private void UpdateMinimapCameraPosition()
        {
            if (_minimapCamera == null || _minimapCameraGo == null) return;

            Vector3 followPosition;
            if (_playerTransform != null)
                followPosition = _playerTransform.position;
            else if (Camera.main != null)
                followPosition = Camera.main.transform.position;
            else
                return;

            var camTransform = _minimapCameraGo.transform;
            camTransform.position = followPosition + Vector3.up * MinimapCameraHeightOffset;
            // 向きは真下のまま（Initialize で設定済み）
        }

        /// <summary>
        /// ワールド座標をミニマップカメラ視点のビューポート座標（0～1）に変換する。
        /// 宝・出口など他のアイコン表示にも再利用可能。
        /// </summary>
        private Vector2 WorldPositionToMinimapViewport(Vector3 worldPosition)
        {
            if (_minimapCamera == null) return new Vector2(0.5f, 0.5f);
            Vector3 vp = _minimapCamera.WorldToViewportPoint(worldPosition);
            float x = Mathf.Clamp01(vp.x);
            float y = Mathf.Clamp01(vp.y);
            return new Vector2(x, y);
        }

        /// <summary>
        /// ビューポート座標（0～1）をミニマップ RawImage 内の anchoredPosition に変換する。
        /// アイコンの anchor が (0.5, 0.5), pivot が (0.5, 0.5) であることを前提とする。
        /// </summary>
        private Vector2 ViewportToMinimapAnchoredPosition(float viewportX, float viewportY)
        {
            if (_minimapRectTransform == null) return Vector2.zero;
            float w = _minimapRectTransform.rect.width;
            float h = _minimapRectTransform.rect.height;
            float x = (viewportX - 0.5f) * w;
            float y = (viewportY - 0.5f) * h;
            return new Vector2(x, y);
        }

        /// <summary>
        /// 上向き（↑）の二等辺三角形を描いた 32x32 の Texture2D を生成し、Sprite にして返す。
        /// 背景は透明、三角形は白。Image.color で味方（緑）・敵（赤）を付ける。
        /// </summary>
        private static Sprite CreateTriangleIconSprite()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Bilinear;
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, clear);
            float cx = (size - 1) * 0.5f;
            float top = size - 1;
            float baseY = 2f;
            float halfW = (size - 1) * 0.5f * 0.85f;
            for (int py = 0; py < size; py++)
            {
                float ny = (top - py) / (top - baseY);
                if (ny < 0f || ny > 1f) continue;
                float halfWidthAt = halfW * ny;
                for (int px = 0; px < size; px++)
                {
                    float dx = Mathf.Abs(px - cx);
                    if (dx <= halfWidthAt)
                        tex.SetPixel(px, py, Color.white);
                }
            }
            tex.Apply();
            var rect = new Rect(0f, 0f, size, size);
            return Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// 塗りつぶしの白い円を描いた 32x32 の Texture2D を生成し、Sprite にして返す。
        /// 背景は透明。宝物・アイテム用（Image.color で黄色を付ける）。
        /// </summary>
        private static Sprite CreateCircleIconSprite()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Bilinear;
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, clear);
            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float radius = (size - 1) * 0.5f * 0.9f;
            float radiusSq = radius * radius;
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float dx = px - cx;
                    float dy = py - cy;
                    if (dx * dx + dy * dy <= radiusSq)
                        tex.SetPixel(px, py, Color.white);
                }
            }
            tex.Apply();
            var rect = new Rect(0f, 0f, size, size);
            return Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// ワールド座標をミニマップビューポートに変換し、画面内（0～1）に収まっているか返す。
        /// 画面内の場合のみ out にビューポート座標を渡す。
        /// </summary>
        private bool TryWorldToMinimapViewport(Vector3 worldPosition, out Vector2 viewport)
        {
            viewport = Vector2.zero;
            if (_minimapCamera == null) return false;
            Vector3 vp = _minimapCamera.WorldToViewportPoint(worldPosition);
            if (vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) return false;
            viewport = new Vector2(vp.x, vp.y);
            return true;
        }

        /// <summary>
        /// ミニマップ上のオーバーレイアイコン（プレイヤー位置・向き、味方・敵レーダー）を更新する。
        /// </summary>
        private void UpdateMinimapIcons()
        {
            UpdatePlayerIcon();
            UpdateRadarIcons();
            UpdateItemIcons();
        }

        private void UpdatePlayerIcon()
        {
            if (_playerIconRect == null) return;
            Transform target = _playerTransform != null ? _playerTransform : (Camera.main != null ? Camera.main.transform : null);
            if (target == null)
            {
                _playerIconRect.gameObject.SetActive(false);
                return;
            }
            _playerIconRect.gameObject.SetActive(true);
            Vector2 viewport = WorldPositionToMinimapViewport(target.position);
            _playerIconRect.anchoredPosition = ViewportToMinimapAnchoredPosition(viewport.x, viewport.y);
            _playerIconRect.localEulerAngles = new Vector3(0f, 0f, -target.eulerAngles.y);
        }

        /// <summary>
        /// 味方（緑三角）・敵（赤三角）をレーダー表示する。ProtoActor を取得し、コンポーネントで味方/敵を判別。
        /// </summary>
        private void UpdateRadarIcons()
        {
            if (_radarIconContainer == null || _radarIconPool == null || _triangleIconSprite == null || _minimapCamera == null)
                return;

            Assembly gameAssembly = _gameAssembly;
            if (gameAssembly == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Assembly-CSharp") { gameAssembly = asm; break; }
                }
            }
            Type protoActorType = gameAssembly?.GetType("Mimic.Actors.ProtoActor");
            Type fishNetDissonanceType = gameAssembly?.GetType("Mimic.Voice.FishNetDissonancePlayer");
            Type monsterHummingType = gameAssembly?.GetType("Mimic.Animation.MonsterHummingSoundPlayer");
            Type dlMovementType = gameAssembly?.GetType("DLAgent.DLMovementAgent");
            Type dlDecisionType = gameAssembly?.GetType("DLAgent.DLDecisionAgent");
            if (protoActorType == null) return;

            UnityEngine.Object[] allActors = UnityEngine.Object.FindObjectsOfType(protoActorType);
            int used = 0;
            foreach (UnityEngine.Object obj in allActors ?? Array.Empty<UnityEngine.Object>())
            {
                var actor = obj as Component;
                if (actor == null) continue;
                if (_playerTransform != null && actor.transform == _playerTransform) continue;
                if (!TryWorldToMinimapViewport(actor.transform.position, out Vector2 viewport)) continue;

                bool isAlly = IsAllyActor(actor, fishNetDissonanceType, monsterHummingType, dlMovementType, dlDecisionType);
                bool isEnemy = !isAlly; // 味方判定をすり抜けた ProtoActor はすべて敵（赤色）とする
                if (isEnemy && !_cfgShowEnemies.Value) continue;
                if (!isEnemy && !_cfgShowAllies.Value) continue;
                Color color = isEnemy ? new Color(1f, 0.2f, 0.2f, 1f) : new Color(0.2f, 0.9f, 0.3f, 1f);

                RadarIconEntry entry = GetOrCreateRadarIconEntry(used);
                entry.Rect.anchoredPosition = ViewportToMinimapAnchoredPosition(viewport.x, viewport.y);
                entry.Rect.localEulerAngles = new Vector3(0f, 0f, -actor.transform.eulerAngles.y);
                entry.Image.color = color;
                entry.Rect.gameObject.SetActive(true);
                used++;
            }
            for (int i = used; i < _radarIconPool.Count; i++)
                _radarIconPool[i].Rect.gameObject.SetActive(false);
        }

        /// <summary>AIコンポーネント・鼻歌・Enemyタグで敵を確実に除外し、Playerタグまたは FishNetDissonancePlayer なら味方。擬態する敵を見破る。</summary>
        private static bool IsAllyActor(Component actor, Type fishNetDissonancePlayerType, Type monsterHummingType, Type dlMovementType, Type dlDecisionType)
        {
            // AI・敵の確実な除外（DLAgent は敵専用）
            if (dlMovementType != null && (actor.GetComponentInChildren(dlMovementType, true) != null || actor.GetComponentInParent(dlMovementType) != null)) return false;
            if (dlDecisionType != null && (actor.GetComponentInChildren(dlDecisionType, true) != null || actor.GetComponentInParent(dlDecisionType) != null)) return false;
            // 鼻歌の除外（敵特有コンポーネント）
            if (monsterHummingType != null && (actor.GetComponentInChildren(monsterHummingType, true) != null || actor.GetComponentInParent(monsterHummingType) != null)) return false;
            // 敵タグの除外（自身または親階層が Enemy）
            if (actor.gameObject.CompareTag("Enemy") || actor.transform.root.CompareTag("Enemy")) return false;
            // 味方の判定（敵条件をすべてすり抜けた上で）
            if (actor.gameObject.CompareTag("Player")) return true;
            if (fishNetDissonancePlayerType != null && actor.GetComponentInChildren(fishNetDissonancePlayerType, true) != null) return true;
            // デフォルトは安全のため敵
            return false;
        }

        private RadarIconEntry GetOrCreateRadarIconEntry(int index)
        {
            while (_radarIconPool.Count <= index)
            {
                var go = new GameObject("RadarIcon");
                go.transform.SetParent(_radarIconContainer, false);
                var rect = go.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(12f, 12f);
                rect.anchoredPosition = Vector2.zero;
                var img = go.AddComponent<Image>();
                img.sprite = _triangleIconSprite;
                img.color = Color.white;
                img.raycastTarget = false;
                _radarIconPool.Add(new RadarIconEntry { Rect = rect, Image = img });
            }
            return _radarIconPool[index];
        }

        /// <summary>
        /// FindObjectsOfType&lt;MonoBehaviour&gt; を1回だけ実行し、_itemTypes と ShouldExclude でフィルタして CachedItemTransforms を更新する。
        /// </summary>
        private void RefreshItemCache()
        {
            if (_itemTypes == null || _itemTypes.Count == 0) return;
            try
            {
                MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                var newCache = new HashSet<Transform>();
                for (int i = 0; i < (all?.Length ?? 0); i++)
                {
                    MonoBehaviour mb = all[i];
                    if (mb == null) continue;
                    if (!_itemTypes.Contains(mb.GetType())) continue;
                    if (ShouldExcludeFromItemCache(mb)) continue;
                    newCache.Add(mb.transform);
                }
                CachedItemTransforms = newCache;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// CachedItemTransforms をループして、黄色い丸アイコンを描画する（毎フレーム・軽量）。null はセットから除去。
        /// </summary>
        private void UpdateItemIcons()
        {
            if (_itemIconContainer == null || _itemIconPool == null || _circleIconSprite == null || _minimapCamera == null)
                return;
            if (!_cfgShowItems.Value)
            {
                for (int i = 0; i < _itemIconPool?.Count; i++) _itemIconPool[i].Rect.gameObject.SetActive(false);
                return;
            }

            var toRemove = new List<Transform>();
            int used = 0;
            foreach (Transform t in CachedItemTransforms)
            {
                if (t == null)
                {
                    toRemove.Add(t);
                    continue;
                }
                // プレイヤー基準の高さフィルター（階層スライス）：同じフロア＋棚の上のみ表示
                float heightDiff = t.position.y - (_playerTransform != null ? _playerTransform.position.y : t.position.y);
                if (_playerTransform != null && (heightDiff < -2.0f || heightDiff > 3.5f))
                    continue;

                if (!TryWorldToMinimapViewport(t.position, out Vector2 viewport)) continue;

                RadarIconEntry entry = GetOrCreateItemIconEntry(used);
                entry.Rect.anchoredPosition = ViewportToMinimapAnchoredPosition(viewport.x, viewport.y);
                entry.Rect.localEulerAngles = Vector3.zero;
                entry.Image.sprite = _circleIconSprite;
                // 棚の上（+2.0～+3.5m）は半透明で「少し高い位置」を視覚的に示す
                float alpha = (_playerTransform != null && heightDiff >= 2.0f && heightDiff <= 3.5f) ? 0.6f : 1f;
                entry.Image.color = new Color(1f, 0.8f, 0f, alpha);
                entry.Rect.gameObject.SetActive(true);
                used++;
            }
            for (int i = 0; i < toRemove.Count; i++)
                CachedItemTransforms.Remove(toRemove[i]);
            for (int i = used; i < _itemIconPool.Count; i++)
                _itemIconPool[i].Rect.gameObject.SetActive(false);
        }

        private RadarIconEntry GetOrCreateItemIconEntry(int index)
        {
            while (_itemIconPool.Count <= index)
            {
                var go = new GameObject("ItemIcon");
                go.transform.SetParent(_itemIconContainer, false);
                var rect = go.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(12f, 12f);
                rect.anchoredPosition = Vector2.zero;
                var img = go.AddComponent<Image>();
                img.sprite = _circleIconSprite;
                img.color = new Color(1f, 0.8f, 0f, 1f);
                img.raycastTarget = false;
                _itemIconPool.Add(new RadarIconEntry { Rect = rect, Image = img });
            }
            return _itemIconPool[index];
        }

        /// <summary>
        /// Assembly-CSharp 内の MonoBehaviour を走査し、「Value/Price/ScrapValue/ItemValue」を持つ型のみ _itemTypes にキャッシュする。
        /// クラス名に Player/Camera/Door/UI/Manager/Controller を含む型は除外する。
        /// </summary>
        private static void ResolveItemTypes()
        {
            if (_itemTypes != null) return;
            _itemTypes = new HashSet<Type>();
            Assembly gameAsm = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetName().Name == "Assembly-CSharp") { gameAsm = asm; break; }
                }
                catch (Exception) { }
            }
            if (gameAsm == null) return;
            Type monoType = typeof(MonoBehaviour);
            string[] valueNames = { "Value", "Price", "ScrapValue", "ItemValue" };
            string[] excludeClassNames = { "Player", "Camera", "Door", "UI", "Manager", "Controller" };
            try
            {
                Type[] types = gameAsm.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t == null || !monoType.IsAssignableFrom(t)) continue;
                    string className = t.Name ?? "";
                    bool excluded = false;
                    for (int e = 0; e < excludeClassNames.Length; e++)
                    {
                        if (className.IndexOf(excludeClassNames[e], StringComparison.OrdinalIgnoreCase) >= 0)
                        { excluded = true; break; }
                    }
                    if (excluded) continue;

                    bool hasValueMember = false;
                    FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    for (int f = 0; f < fields.Length; f++)
                    {
                        string fn = fields[f].Name ?? "";
                        for (int v = 0; v < valueNames.Length; v++)
                        {
                            if (fn.IndexOf(valueNames[v], StringComparison.OrdinalIgnoreCase) >= 0)
                            { hasValueMember = true; break; }
                        }
                        if (hasValueMember) break;
                    }
                    if (!hasValueMember)
                    {
                        PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        for (int p = 0; p < props.Length; p++)
                        {
                            string pn = props[p].Name ?? "";
                            for (int v = 0; v < valueNames.Length; v++)
                            {
                                if (pn.IndexOf(valueNames[v], StringComparison.OrdinalIgnoreCase) >= 0)
                                { hasValueMember = true; break; }
                            }
                            if (hasValueMember) break;
                        }
                    }
                    if (hasValueMember)
                        _itemTypes.Add(t);
                }
                if (_itemTypes != null && _itemTypes.Count > 0)
                    MelonLogger.Msg($"MimesisMinimap: アイテム型を {_itemTypes.Count} 件キャッシュしました（Value/Price 等を持つ型）。");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"MimesisMinimap: アイテム型の解決に失敗しました: {ex.Message}");
            }
        }

        private void DestroyMinimapUI()
        {
            if (_minimapCamera != null) _minimapCamera.targetTexture = null;
            if (_minimapRenderTexture != null)
            {
                _minimapRenderTexture.Release();
                UnityEngine.Object.Destroy(_minimapRenderTexture);
                _minimapRenderTexture = null;
            }
            if (_minimapRawImage != null) _minimapRawImage.texture = null;
            if (_minimapCameraGo != null) { UnityEngine.Object.Destroy(_minimapCameraGo); _minimapCameraGo = null; }
            _minimapCamera = null;
            _minimapLight = null;
            _playerIconRect = null;
            if (_triangleIconSprite != null && _triangleIconSprite.texture != null)
                UnityEngine.Object.Destroy(_triangleIconSprite.texture);
            if (_triangleIconSprite != null) { UnityEngine.Object.Destroy(_triangleIconSprite); _triangleIconSprite = null; }
            if (_circleIconSprite != null && _circleIconSprite.texture != null)
                UnityEngine.Object.Destroy(_circleIconSprite.texture);
            if (_circleIconSprite != null) { UnityEngine.Object.Destroy(_circleIconSprite); _circleIconSprite = null; }
            _radarIconPool?.Clear();
            _radarIconPool = null;
            _radarIconContainer = null;
            _itemIconPool?.Clear();
            _itemIconPool = null;
            _itemIconContainer = null;
            if (_minimapCanvas != null) { UnityEngine.Object.Destroy(_minimapCanvas.gameObject); _minimapCanvas = null; }
            _minimapRawImage = null;
            _minimapRectTransform = null;
        }

        private void RegisterMinimapLightCallbacks()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private void UnregisterMinimapLightCallbacks()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != _minimapCamera || _minimapLight == null) return;
            _minimapLight.enabled = true;
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != _minimapCamera || _minimapLight == null) return;
            _minimapLight.enabled = false;
        }

        /// <summary>
        /// ローカルプレイヤーの Transform を解決する。
        /// マルチ（FishNet）では Camera.main が追従している対象、または ProtoActor の IsOwner を優先する。
        /// </summary>
        private void ResolvePlayerTransform()
        {
            // 1) Camera.main が追従している対象（Cinemachine 等のターゲット）を探す
            if (Camera.main != null)
            {
                Transform camTarget = GetMainCameraFollowTarget();
                if (camTarget != null)
                {
                    _playerTransform = camTarget;
                    MelonLogger.Msg("MimesisMinimap: プレイヤーを Camera.main の追従ターゲットで検出しました。");
                    return;
                }
                // 追従ターゲットが取れない場合は Camera.main の位置を基準にし、後述の ProtoActor で補う
            }

            // 2) Mimic.Actors.ProtoActor のうち IsOwner（FishNet）またはローカル判定のものを探す
            Assembly gameAssembly = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Assembly-CSharp")
                {
                    gameAssembly = asm;
                    break;
                }
            }

            if (gameAssembly != null)
            {
                Type protoActorType = gameAssembly.GetType("Mimic.Actors.ProtoActor");
                if (protoActorType != null)
                {
                    Transform localActorTransform = TryGetLocalProtoActorTransform(protoActorType, gameAssembly);
                    if (localActorTransform != null)
                    {
                        _playerTransform = localActorTransform;
                        MelonLogger.Msg("MimesisMinimap: プレイヤーを Mimic.Actors.ProtoActor（ローカル／IsOwner）で検出しました。");
                        return;
                    }
                    // フォールバック: 単一の ProtoActor（シングルプレイ想定）
                    UnityEngine.Object instance = UnityEngine.Object.FindObjectOfType(protoActorType);
                    if (instance != null)
                    {
                        var transformProp = protoActorType.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                        if (transformProp != null)
                        {
                            _playerTransform = transformProp.GetValue(instance, null) as Transform;
                            if (_playerTransform != null)
                            {
                                MelonLogger.Msg("MimesisMinimap: プレイヤーを Mimic.Actors.ProtoActor（単一）で検出しました。");
                                return;
                            }
                        }
                    }
                }
            }

            // 3) Tag "Player"（他プレイヤーと被る可能性あり）
            GameObject go = GameObject.FindWithTag("Player");
            if (go != null)
            {
                _playerTransform = go.transform;
                MelonLogger.Msg("MimesisMinimap: プレイヤーを Tag 'Player' で検出しました。");
                return;
            }

            // 4) CharacterController の単一インスタンス
            var cc = UnityEngine.Object.FindObjectOfType<CharacterController>();
            if (cc != null)
            {
                _playerTransform = cc.transform;
                MelonLogger.Msg("MimesisMinimap: プレイヤーを CharacterController で検出しました。");
            }

            // プレイヤーが未検出でも、Camera.main があれば Update ではその位置を基準にミニマップカメラを動かす（OnUpdate で Camera.main をフォールバック使用）
        }

        /// <summary>Camera.main が追従しているターゲット Transform を取得（Cinemachine 等を想定）。</summary>
        private static Transform GetMainCameraFollowTarget()
        {
            if (Camera.main == null) return null;
            Transform camTransform = Camera.main.transform;
            // 親が「カメラの持ち主」で、その親がプレイヤーである場合がある
            if (camTransform.parent != null)
            {
                Transform parent = camTransform.parent;
                // Cinemachine の Virtual Camera は Follow にターゲットを設定していることが多い
                Type cinemachineVCam = Type.GetType("Cinemachine.CinemachineVirtualCamera, Assembly-CSharp", false)
                    ?? Type.GetType("Cinemachine.CinemachineVirtualCamera, Cinemachine", false);
                if (cinemachineVCam != null)
                {
                    var followProp = cinemachineVCam.GetProperty("Follow", BindingFlags.Public | BindingFlags.Instance);
                    if (followProp != null)
                    {
                        var vcam = parent.GetComponent(cinemachineVCam);
                        if (vcam != null)
                        {
                            object followTarget = followProp.GetValue(vcam, null);
                            if (followTarget is Transform t && t != null)
                                return t;
                        }
                    }
                }
                // カメラの親自体をプレイヤーとみなす（よくある構成）
                if (parent.GetComponent<CharacterController>() != null || parent.CompareTag("Player"))
                    return parent;
            }
            return null;
        }

        /// <summary>ProtoActor の一覧から、IsOwner またはローカル判定に合うものの Transform を返す。</summary>
        private static Transform TryGetLocalProtoActorTransform(Type protoActorType, Assembly gameAssembly)
        {
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(protoActorType);
            if (all == null || all.Length == 0) return null;

            // FishNet: ProtoActor が NetworkBehaviour を継承していれば IsOwner プロパティを持つ
            PropertyInfo isOwnerProp = protoActorType.GetProperty("IsOwner", BindingFlags.Public | BindingFlags.Instance);

            for (int i = 0; i < all.Length; i++)
            {
                var actor = all[i];
                if (actor == null) continue;
                if (isOwnerProp != null)
                {
                    try
                    {
                        bool isOwner = (bool)isOwnerProp.GetValue(actor, null);
                        if (isOwner)
                        {
                            var tr = (actor as Component)?.transform;
                            if (tr != null) return tr;
                        }
                    }
                    catch (Exception) { }
                }
            }

            // IsOwner が使えない場合: Camera.main に最も近い ProtoActor をローカルプレイヤーとみなす
            if (Camera.main != null && all.Length > 0)
            {
                Vector3 camPos = Camera.main.transform.position;
                float bestSq = float.MaxValue;
                Transform best = null;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is Component c && c != null)
                    {
                        float sq = (c.transform.position - camPos).sqrMagnitude;
                        if (sq < bestSq) { bestSq = sq; best = c.transform; }
                    }
                }
                return best;
            }

            return null;
        }

        #region DunGen リフレクション（Harmony／将来拡張用。描画は RenderTexture に一本化）

        private static void ResolveDunGenReflection()
        {
            if (_dunGenReflectionResolved || _dunGenReflectionFailed) return;
            _dunGenReflectionResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm == null) continue;
                        var an = asm.GetName();
                        if (an != null && an.Name == "Assembly-CSharp") { _gameAssembly = asm; break; }
                    }
                    catch (Exception) { }
                }
                if (_gameAssembly == null) { _dunGenReflectionFailed = true; return; }
                _dungeonGeneratorType = _gameAssembly.GetType("DunGen.DungeonGenerator");
                _dungeonType = _gameAssembly.GetType("DunGen.Dungeon");
                _tileType = _gameAssembly.GetType("DunGen.Tile");
                _tilePlacementDataType = _gameAssembly.GetType("DunGen.TilePlacementData");
                _runtimeDungeonType = _gameAssembly.GetType("DunGen.RuntimeDungeon");
                _adjacentRoomCullingType = _gameAssembly.GetType("DunGen.AdjacentRoomCulling");
                if (_dungeonGeneratorType == null || _dungeonType == null || _tileType == null || _tilePlacementDataType == null)
                {
                    _dunGenReflectionFailed = true;
                    return;
                }
                _currentDungeonProp = _dungeonGeneratorType.GetProperty("CurrentDungeon", BindingFlags.Public | BindingFlags.Instance);
                _dungeonAllTilesField = _dungeonType.GetField("allTiles", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_dungeonAllTilesField == null)
                    _dungeonAllTilesField = _dungeonType.GetField("<AllTiles>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                _tilePlacementField = _tileType.GetField("placement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _placementWorldBoundsField = _tilePlacementDataType?.GetField("worldBounds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _placementLocalBoundsField = _tilePlacementDataType?.GetField("localBounds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _placementPositionField = _tilePlacementDataType?.GetField("position", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _placementRotationField = _tilePlacementDataType?.GetField("rotation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _generatorRootField = _dungeonGeneratorType?.GetField("Root", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _runtimeDungeonRootField = _runtimeDungeonType?.GetField("Root", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Type unityUtilType = _gameAssembly?.GetType("DunGen.UnityUtil");
                _unityUtilTransformBoundsMethod = unityUtilType?.GetMethod("TransformBounds", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(Transform), typeof(Bounds) }, null);
                _runtimeDungeonGeneratorField = _runtimeDungeonType?.GetField("Generator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _adjacentCullingDungeonField = _adjacentRoomCullingType?.GetField("dungeon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _adjacentCullingGeneratorField = _adjacentRoomCullingType?.GetField("generator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _adjacentCullingAllTilesField = _adjacentRoomCullingType?.GetField("allTiles", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _adjacentCullingAllDoorsField = _adjacentRoomCullingType?.GetField("allDoors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _dungeonConnectionsField = _dungeonType.GetField("connections", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_dungeonConnectionsField == null)
                    _dungeonConnectionsField = _dungeonType.GetField("<Connections>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                _dungeonAttachmentTileField = _dungeonType.GetField("attachmentTile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_dungeonAttachmentTileField == null)
                    _dungeonAttachmentTileField = _dungeonType.GetField("<AttachmentTile>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                _doorType = _gameAssembly.GetType("DunGen.Door");
                if (_doorType != null)
                {
                    _doorDoorwayAField = _doorType.GetField("DoorwayA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_doorDoorwayAField == null) _doorDoorwayAField = _doorType.GetField("doorwayA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _doorDoorwayBField = _doorType.GetField("DoorwayB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_doorDoorwayBField == null) _doorDoorwayBField = _doorType.GetField("doorwayB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                _doorwayConnectionType = _gameAssembly.GetType("DunGen.DoorwayConnection");
                if (_doorwayConnectionType != null)
                {
                    _connAProp = _doorwayConnectionType.GetProperty("A", BindingFlags.Public | BindingFlags.Instance);
                    if (_connAProp == null) _connAProp = _doorwayConnectionType.GetProperty("a", BindingFlags.Public | BindingFlags.Instance);
                    _connBProp = _doorwayConnectionType.GetProperty("B", BindingFlags.Public | BindingFlags.Instance);
                    if (_connBProp == null) _connBProp = _doorwayConnectionType.GetProperty("b", BindingFlags.Public | BindingFlags.Instance);
                    _connAField = _doorwayConnectionType.GetField("a", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _connBField = _doorwayConnectionType.GetField("b", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                _doorwayType = _gameAssembly.GetType("DunGen.Doorway");
                if (_doorwayType != null)
                    _doorwayTransformProp = _doorwayType.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                _generatorAttachmentSettingsProp = _dungeonGeneratorType.GetProperty("AttachmentSettings", BindingFlags.Public | BindingFlags.Instance);
                _attachmentSettingsType = _gameAssembly.GetType("DunGen.DungeonAttachmentSettings");
                if (_attachmentSettingsType != null)
                    _attachmentDoorwayProp = _attachmentSettingsType.GetProperty("AttachmentDoorway", BindingFlags.Public | BindingFlags.Instance);
                _tileGetExitDoorwayMethod = _tileType?.GetMethod("GetExitDoorway", Type.EmptyTypes);
                _tileGetEntranceDoorwayMethod = _tileType?.GetMethod("GetEntranceDoorway", Type.EmptyTypes);
                _doorwayConnectedDoorwayField = _doorwayType?.GetField("connectedDoorway", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_currentDungeonProp == null || _dungeonAllTilesField == null || _tilePlacementField == null || _placementWorldBoundsField == null)
                    _dunGenReflectionFailed = true;
            }
            catch (Exception)
            {
                _dunGenReflectionFailed = true;
            }
        }

        private static UnityEngine.Object FindObjectOfTypeMaybeInactive(Type type)
        {
            if (type == null) return null;
            try
            {
                var findInactive = Type.GetType("UnityEngine.FindObjectsInactive, UnityEngine.CoreModule", false);
                if (findInactive == null) return SafeFindObjectOfType(type);
                object includeInactive = null;
                try { includeInactive = Enum.Parse(findInactive, "Include"); } catch (Exception) { }
                if (includeInactive == null) return SafeFindObjectOfType(type);
                var method = typeof(UnityEngine.Object).GetMethod("FindFirstObjectByType", new[] { typeof(Type), findInactive });
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { type, includeInactive });
                    return result as UnityEngine.Object;
                }
            }
            catch (Exception) { }
            return SafeFindObjectOfType(type);
        }

        private static UnityEngine.Object SafeFindObjectOfType(Type type)
        {
            if (type == null) return null;
            try { return UnityEngine.Object.FindObjectOfType(type); }
            catch (Exception) { return null; }
        }

        private System.Collections.IList GetAllTilesFromDungeon(object dungeon)
        {
            if (dungeon == null || _dungeonAllTilesField == null) return null;
            try
            {
                object allTilesObj = _dungeonAllTilesField.GetValue(dungeon);
                return allTilesObj as System.Collections.IList;
            }
            catch (Exception) { return null; }
        }

        private static Vector3 GetDoorwayWorldPosition(object doorwayObj)
        {
            if (doorwayObj == null) return Vector3.zero;
            try
            {
                if (doorwayObj is Component comp && comp != null)
                    return comp.transform.position;
                if (_doorwayTransformProp == null) return Vector3.zero;
                var t = _doorwayTransformProp.GetValue(doorwayObj, null) as Transform;
                return t != null ? t.position : Vector3.zero;
            }
            catch (Exception) { return Vector3.zero; }
        }

        private List<Bounds> BoundsListFromTiles(System.Collections.IList list, Transform dungeonRoot)
        {
            if (list == null || list.Count == 0 || _tilePlacementField == null || _placementWorldBoundsField == null) return null;
            bool useTileTransform = _placementLocalBoundsField != null && _placementPositionField != null && _placementRotationField != null && _unityUtilTransformBoundsMethod != null;
            GameObject tempTileGo = null;
            if (useTileTransform)
            {
                tempTileGo = new GameObject("MimesisMinimap_TileBoundsTemp");
                tempTileGo.SetActive(false);
            }
            var result = new List<Bounds>();
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        object tile = list[i];
                        if (tile == null) continue;
                        object placement = _tilePlacementField.GetValue(tile);
                        if (placement == null) continue;
                        bool added = false;
                        if (useTileTransform && tempTileGo != null)
                        {
                            object posObj = _placementPositionField.GetValue(placement);
                            object rotObj = _placementRotationField.GetValue(placement);
                            object localObj = _placementLocalBoundsField.GetValue(placement);
                            if (posObj is Vector3 pos && rotObj is Quaternion rot && localObj is Bounds localB)
                            {
                                tempTileGo.transform.SetParent(null);
                                tempTileGo.transform.position = pos;
                                tempTileGo.transform.rotation = rot;
                                object transformed = _unityUtilTransformBoundsMethod.Invoke(null, new object[] { tempTileGo.transform, localB });
                                if (transformed is Bounds b)
                                {
                                    result.Add(b);
                                    added = true;
                                }
                            }
                        }
                        if (!added)
                        {
                            object boundsObj = _placementWorldBoundsField.GetValue(placement);
                            if (boundsObj is Bounds wb)
                                result.Add(wb);
                        }
                    }
                    catch (Exception) { }
                }
            }
            finally
            {
                if (tempTileGo != null)
                    UnityEngine.Object.Destroy(tempTileGo);
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>DunGen から現在ダンジョンのマップデータを取得（Harmony／将来のマーカー等で利用可能）。</summary>
        private DunGenMapData TryGetDungeonMapData()
        {
            if (_cachedDungeonMapData != null)
                return _cachedDungeonMapData;

            string currentSceneName = SceneManager.GetActiveScene().name;
            if (!IsMinimapNeededForScene(currentSceneName))
                return null;
            if (currentSceneName != null && currentSceneName == _dunGenNegativeCacheSceneName)
                return null;
            if (_lastDunGenTryFrame >= 0 && (Time.frameCount - _lastDunGenTryFrame) < DunGenRetryIntervalFrames)
                return null;

            ResolveDunGenReflection();
            if (_dunGenReflectionFailed || _dungeonGeneratorType == null) return null;
            try
            {
                object dungeon = null;
                System.Collections.IList allTiles = null;
                UnityEngine.Object genObj = FindObjectOfTypeMaybeInactive(_dungeonGeneratorType);
                if (genObj != null && _currentDungeonProp != null)
                {
                    try { dungeon = _currentDungeonProp.GetValue(genObj, null); } catch (Exception) { }
                }
                if (dungeon == null && _runtimeDungeonType != null && _runtimeDungeonGeneratorField != null)
                {
                    var runtimeObj = FindObjectOfTypeMaybeInactive(_runtimeDungeonType);
                    if (runtimeObj != null)
                    {
                        try
                        {
                            object gen = _runtimeDungeonGeneratorField.GetValue(runtimeObj);
                            if (gen != null && _currentDungeonProp != null)
                                dungeon = _currentDungeonProp.GetValue(gen, null);
                        }
                        catch (Exception) { }
                    }
                }
                UnityEngine.Object cullingObj = null;
                if (dungeon == null && _adjacentRoomCullingType != null)
                {
                    cullingObj = FindObjectOfTypeMaybeInactive(_adjacentRoomCullingType);
                    if (cullingObj != null && _adjacentCullingDungeonField != null)
                        dungeon = _adjacentCullingDungeonField.GetValue(cullingObj);
                    if (dungeon == null && cullingObj != null && _adjacentCullingGeneratorField != null && _currentDungeonProp != null)
                    {
                        object gen = _adjacentCullingGeneratorField.GetValue(cullingObj);
                        if (gen != null) dungeon = _currentDungeonProp.GetValue(gen, null);
                    }
                    if (allTiles == null && cullingObj != null && _adjacentCullingAllTilesField != null)
                        allTiles = _adjacentCullingAllTilesField.GetValue(cullingObj) as System.Collections.IList;
                }
                if (dungeon != null && allTiles == null)
                    allTiles = GetAllTilesFromDungeon(dungeon);

                List<Bounds> tileList = BoundsListFromTiles(allTiles, null);
                if (tileList != null && tileList.Count > 0)
                {
                    _cachedDungeonMapData = new DunGenMapData { Tiles = tileList };
                    return _cachedDungeonMapData;
                }
                if (genObj == null && cullingObj == null)
                    _dunGenNegativeCacheSceneName = currentSceneName;
                else
                    _lastDunGenTryFrame = Time.frameCount;
            }
            catch (Exception) { }
            return null;
        }

        private sealed class DunGenMapData
        {
            public List<Bounds> Tiles;
            public List<KeyValuePair<Vector3, Vector3>> DoorSegments;
            public Vector3? EntranceWorldPos;
            public List<Vector3> EntranceWorldPositions;
        }

        #endregion
    }
}
