using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 씬에 별도 오브젝트를 배치하지 않아도, 장비 UI/이펙트를 자동으로 생성한다.
/// - U: 장비 슬롯 UI 토글
/// - UI는 현재 무기(검/활)를 하이라이트 표시
/// - 전투/대쉬 이벤트(PlayerActionEvent)를 읽어 간단한 모션/VFX를 생성
/// </summary>
public static class EquipmentBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        // 중복 생성 방지
        if (GameObject.Find("__EquipmentBootstrap") != null) return;

        var root = new GameObject("__EquipmentBootstrap");
        Object.DontDestroyOnLoad(root);

        root.AddComponent<EquipmentUIController>();
        root.AddComponent<EquipmentVfxController>();
    }
}

/// <summary>
/// U키로 열고 닫을 수 있는 장비 슬롯 UI (무기 2칸 + 신발 1칸)
/// </summary>
public class EquipmentUIController : MonoBehaviour
{
    private World _world;
    private EntityManager _em;
    private EntityQuery _playerQuery;
    private Entity _player;

    // 일부 Entities 버전에서는 EntityQuery.IsCreated가 없다.
    // 따라서 쿼리 생성 여부를 별도 플래그로 관리한다.
    private bool _queryReady;

    private GameObject _panel;
    private Image _slotSword;
    private Image _slotBow;
    private Image _slotShoes;

    private void Awake()
    {
        BuildUI();
        _panel.SetActive(false);
    }

    private void Start()
    {
        EnsureQuery();
    }

    private void EnsureQuery()
    {
        World world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
        {
            _queryReady = false;
            _player = Entity.Null;
            return;
        }

        if (_queryReady && _world == world) return;

        _world = world;
        _em = world.EntityManager;
        _playerQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<PlayerEquipment>(),
            ComponentType.ReadOnly<LocalTransform>());

        _queryReady = true;
        _player = Entity.Null;
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.uKey.wasPressedThisFrame)
            _panel.SetActive(!_panel.activeSelf);

        EnsureQuery();
        if (!_queryReady) return;

        if (_player == Entity.Null || !_em.Exists(_player))
        {
            using var entities = _playerQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            _player = entities.Length > 0 ? entities[0] : Entity.Null;
        }
        if (_player == Entity.Null || !_em.Exists(_player)) return;

        var equip = _em.GetComponentData<PlayerEquipment>(_player);
        Highlight((WeaponKind)equip.CurrentWeapon);
    }

    private void Highlight(WeaponKind current)
    {
        // 선택된 슬롯은 밝게, 나머지는 어둡게
        SetSlotEmphasis(_slotSword, current == WeaponKind.Sword);
        SetSlotEmphasis(_slotBow, current == WeaponKind.Bow);
        SetSlotEmphasis(_slotShoes, true); // 신발은 항상 착용
    }

    private static void SetSlotEmphasis(Image slot, bool active)
    {
        if (slot == null) return;
        // 배경만 하이라이트 (아이콘은 항상 그대로 보이도록 유지)
        slot.color = active
            ? new Color(1f, 1f, 1f, 1f)
            : new Color(0.35f, 0.35f, 0.35f, 0.6f);
    }

    private void BuildUI()
    {
        // Canvas
        var canvasGO = new GameObject("EquipmentCanvas");
        canvasGO.transform.SetParent(transform, false);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        // Panel
        _panel = new GameObject("EquipmentPanel");
        _panel.transform.SetParent(canvasGO.transform, false);
        var panelImage = _panel.AddComponent<Image>();
        panelImage.color = new Color(0, 0, 0, 0.45f);

        var panelRT = _panel.GetComponent<RectTransform>();
        panelRT.anchorMin = new Vector2(0, 1);
        panelRT.anchorMax = new Vector2(0, 1);
        panelRT.pivot = new Vector2(0, 1);
        panelRT.anchoredPosition = new Vector2(16, -16);
        panelRT.sizeDelta = new Vector2(230, 90);

        // Slots
        _slotSword = CreateSlot(_panel.transform, new Vector2(20, -18), MakeSwordIcon());
        _slotBow = CreateSlot(_panel.transform, new Vector2(86, -18), MakeBowIcon());
        _slotShoes = CreateSlot(_panel.transform, new Vector2(152, -18), MakeShoeIcon());
    }

    private static Image CreateSlot(Transform parent, Vector2 pos, Sprite icon)
    {
        var slotGO = new GameObject("Slot");
        slotGO.transform.SetParent(parent, false);

        var bg = slotGO.AddComponent<Image>();
        bg.color = new Color(1f, 1f, 1f, 1f);

        var rt = slotGO.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(56, 56);

        var iconGO = new GameObject("Icon");
        iconGO.transform.SetParent(slotGO.transform, false);
        var iconImg = iconGO.AddComponent<Image>();
        iconImg.sprite = icon;
        iconImg.preserveAspect = true;

        var irt = iconGO.GetComponent<RectTransform>();
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(8, 8);
        irt.offsetMax = new Vector2(-8, -8);

        return bg;
    }

    // --- 아이콘(간단한 픽셀 드로잉) ---
    private static Sprite MakeSwordIcon()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Clear(tex);
        // blade
        DrawRect(tex, 30, 10, 4, 42, new Color(0.85f, 0.85f, 0.9f, 1));
        // guard
        DrawRect(tex, 22, 28, 20, 4, new Color(0.6f, 0.45f, 0.2f, 1));
        // handle
        DrawRect(tex, 30, 48, 4, 10, new Color(0.25f, 0.18f, 0.12f, 1));
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
    }

    private static Sprite MakeBowIcon()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Clear(tex);
        // bow arc
        for (int y = 10; y < 54; y++)
        {
            float t = (y - 10) / 44f; // 0..1
            int x = 18 + (int)(12 * Mathf.Sin(t * Mathf.PI));
            DrawRect(tex, x, y, 3, 2, new Color(0.55f, 0.35f, 0.15f, 1));
        }
        // string
        DrawLine(tex, new Vector2Int(18, 10), new Vector2Int(18, 54), new Color(0.95f, 0.95f, 0.95f, 1));
        // arrow hint
        DrawRect(tex, 34, 30, 18, 2, new Color(0.85f, 0.85f, 0.9f, 1));
        DrawRect(tex, 52, 29, 2, 4, new Color(0.85f, 0.85f, 0.9f, 1));
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
    }

    private static Sprite MakeShoeIcon()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Clear(tex);
        // boot body
        DrawRect(tex, 20, 24, 18, 24, new Color(0.15f, 0.2f, 0.35f, 1));
        // sole
        DrawRect(tex, 20, 48, 28, 6, new Color(0.1f, 0.1f, 0.1f, 1));
        // toe
        DrawRect(tex, 38, 38, 14, 16, new Color(0.15f, 0.2f, 0.35f, 1));
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
    }

    private static void Clear(Texture2D tex)
    {
        var clear = new Color(0, 0, 0, 0);
        var pixels = new Color[tex.width * tex.height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;
        tex.SetPixels(pixels);
    }

    private static void DrawRect(Texture2D tex, int x, int y, int w, int h, Color c)
    {
        for (int iy = y; iy < y + h; iy++)
        for (int ix = x; ix < x + w; ix++)
        {
            if (ix < 0 || iy < 0 || ix >= tex.width || iy >= tex.height) continue;
            tex.SetPixel(ix, tex.height - 1 - iy, c);
        }
    }

    private static void DrawLine(Texture2D tex, Vector2Int a, Vector2Int b, Color c)
    {
        int dx = Mathf.Abs(b.x - a.x);
        int dy = Mathf.Abs(b.y - a.y);
        int sx = a.x < b.x ? 1 : -1;
        int sy = a.y < b.y ? 1 : -1;
        int err = dx - dy;

        int x = a.x;
        int y = a.y;
        while (true)
        {
            DrawRect(tex, x, y, 1, 1, c);
            if (x == b.x && y == b.y) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
        }
    }
}

/// <summary>
/// PlayerActionEvent 버퍼를 읽어 간단한 모션/VFX를 만든다.
/// (테스트용: 큐브/실린더 프리미티브 사용)
/// </summary>
public class EquipmentVfxController : MonoBehaviour
{
    private World _world;
    private EntityManager _em;
    private EntityQuery _playerQuery;
    private Entity _player;

    private bool _queryReady;

    private Transform _vfxRoot;

    private void Start()
    {
        _vfxRoot = new GameObject("EquipmentVFX").transform;
        _vfxRoot.SetParent(transform, false);

        EnsureQuery();
    }

    private void EnsureQuery()
    {
        World world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
        {
            _queryReady = false;
            _player = Entity.Null;
            return;
        }

        if (_queryReady && _world == world) return;

        _world = world;
        _em = world.EntityManager;
        _playerQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadWrite<PlayerActionEvent>(),
            ComponentType.ReadOnly<PlayerEquipment>(),
            ComponentType.ReadOnly<LocalTransform>());

        _queryReady = true;
        _player = Entity.Null;
    }

    private void Update()
    {
        EnsureQuery();
        if (!_queryReady) return;

        if (_player == Entity.Null || !_em.Exists(_player))
        {
            using var entities = _playerQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            _player = entities.Length > 0 ? entities[0] : Entity.Null;
        }
        if (_player == Entity.Null || !_em.Exists(_player)) return;

        if (!_em.HasBuffer<PlayerActionEvent>(_player)) return;
        var buffer = _em.GetBuffer<PlayerActionEvent>(_player);
        if (buffer.Length == 0) return;

        // 안전하게 복사 후 비우기
        var tmp = new PlayerActionEvent[buffer.Length];
        for (int i = 0; i < buffer.Length; i++) tmp[i] = buffer[i];
        buffer.Clear();

        for (int i = 0; i < tmp.Length; i++)
        {
            var ev = tmp[i];
            switch ((PlayerActionType)ev.ActionType)
            {
                case PlayerActionType.SwordAttack:
                    SpawnSwordSlash(ev.Position, ev.Direction, false);
                    break;
                case PlayerActionType.SwordSkill:
                    SpawnSwordSlash(ev.Position, ev.Direction, true);
                    break;
                case PlayerActionType.BowAttack:
                    SpawnArrow(ev.Position, ev.Direction, 14f);
                    break;
                case PlayerActionType.BowSkill:
                    SpawnArrowFan(ev.Position, ev.Direction, 7, 35f);
                    break;
                case PlayerActionType.Dash:
                    SpawnDashTrail(ev.Position, ev.Direction);
                    break;
            }
        }
    }

    private void SpawnSwordSlash(float3 pos, float3 dir, bool circle)
    {
        // 큐브 1개를 잠깐 생성해서 회전/스케일 변화로 베기 느낌만 준다.
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = circle ? "SwordSkill" : "SwordAttack";
        go.transform.SetParent(_vfxRoot, false);
        go.transform.position = (Vector3)pos + (Vector3)(dir * 1.0f) + Vector3.up * 0.6f;

        if (circle)
        {
            go.transform.localScale = new Vector3(2.5f, 0.05f, 2.5f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
        else
        {
            go.transform.localScale = new Vector3(1.8f, 0.05f, 0.25f);
            go.transform.rotation = Quaternion.LookRotation((Vector3)dir) * Quaternion.Euler(0f, 0f, 25f);
        }

        var fx = go.AddComponent<OneShotFx>();
        fx.duration = circle ? 0.35f : 0.2f;
        fx.spin = circle ? new Vector3(0f, 540f, 0f) : new Vector3(0f, 0f, -720f);
        fx.grow = circle ? new Vector3(0.6f, 0f, 0.6f) : new Vector3(0.2f, 0f, 0.1f);
    }

    private void SpawnArrow(float3 pos, float3 dir, float speed)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "Arrow";
        go.transform.SetParent(_vfxRoot, false);
        go.transform.position = (Vector3)pos + Vector3.up * 0.8f;
        go.transform.localScale = new Vector3(0.08f, 0.5f, 0.08f);
        go.transform.rotation = Quaternion.LookRotation((Vector3)dir) * Quaternion.Euler(90f, 0f, 0f);

        var proj = go.AddComponent<SimpleProjectile>();
        proj.direction = (Vector3)dir;
        proj.speed = speed;
        proj.lifetime = 1.2f;
    }

    private void SpawnArrowFan(float3 pos, float3 dir, int count, float angleDeg)
    {
        // 애쉬 W 느낌: 부채꼴로 여러 발
        if (count < 3) count = 3;
        float half = angleDeg * 0.5f;
        for (int i = 0; i < count; i++)
        {
            float t = (count == 1) ? 0.5f : (i / (float)(count - 1));
            float yaw = Mathf.Lerp(-half, half, t);
            var rotated = Quaternion.AngleAxis(yaw, Vector3.up) * (Vector3)dir;
            SpawnArrow(pos, (float3)rotated, 12f);
        }
    }

    private void SpawnDashTrail(float3 pos, float3 dir)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "DashTrail";
        go.transform.SetParent(_vfxRoot, false);
        go.transform.position = (Vector3)pos + Vector3.up * 0.35f;
        go.transform.localScale = new Vector3(0.6f, 0.05f, 0.6f);
        go.transform.rotation = Quaternion.LookRotation((Vector3)dir);

        var fx = go.AddComponent<OneShotFx>();
        fx.duration = 0.25f;
        fx.spin = new Vector3(0f, 360f, 0f);
        fx.grow = new Vector3(-0.25f, 0f, -0.25f);
    }
}

/// <summary>단순 투사체 이동</summary>
public class SimpleProjectile : MonoBehaviour
{
    public Vector3 direction = Vector3.forward;
    public float speed = 10f;
    public float lifetime = 1f;

    private float _t;

    private void Update()
    {
        _t += Time.deltaTime;
        transform.position += direction.normalized * (speed * Time.deltaTime);
        if (_t >= lifetime) Destroy(gameObject);
    }
}

/// <summary>잠깐 보여주고 사라지는 1회성 FX</summary>
public class OneShotFx : MonoBehaviour
{
    public float duration = 0.2f;
    public Vector3 spin = Vector3.zero;   // degrees/sec
    public Vector3 grow = Vector3.zero;   // scale delta per sec

    private float _t;
    private Vector3 _baseScale;

    private void Start()
    {
        _baseScale = transform.localScale;
    }

    private void Update()
    {
        _t += Time.deltaTime;
        transform.Rotate(spin * Time.deltaTime, Space.Self);
        transform.localScale = _baseScale + grow * _t;

        // 알파 페이드(머티리얼이 있으면)
        var r = GetComponent<Renderer>();
        if (r != null && r.material != null)
        {
            var c = r.material.color;
            c.a = Mathf.Lerp(1f, 0f, _t / Mathf.Max(0.0001f, duration));
            r.material.color = c;
        }

        if (_t >= duration) Destroy(gameObject);
    }
}
