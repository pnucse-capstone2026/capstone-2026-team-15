using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Swarm
{
    /// <summary>
    /// Test_Scene 에서 Play 중 편의 기능을 제공하는 경량 도구.
    /// - 투사체(원거리 GameObject) 렌더링을 끈 상태로 시작할 수 있다.
    /// - F12 로 스크린샷을 저장한다.
    /// 씬의 아무 GameObject 에 붙여서 사용한다.
    /// (메뉴 Tools ▸ Test_Scene Toolbox 추가 로 바로 추가 가능)
    /// </summary>
    public sealed class TestSceneToolbox : MonoBehaviour
    {
        [Header("투사체")]
        [Tooltip("켜면 Play 시작 시 원거리 투사체 렌더링을 끈다. 전투 판정과 사격 애니메이션은 그대로 실행된다.")]
        public bool hideProjectiles = true;

        [Header("스크린샷")]
        [Tooltip("스크린샷 촬영 키.")]
        public Key screenshotKey = Key.F12;

        [Tooltip("해상도 배율. 2 이면 화면의 2배 해상도로 저장된다.")]
        [Min(1)] public int screenshotSuperSize = 1;

        private void Start()
        {
            if (hideProjectiles)
            {
                RangedProjectileVisualSystem.RenderingEnabled = false;
                Debug.Log("[TestToolbox] 투사체 렌더링 OFF");
            }
        }

        private void OnDestroy()
        {
            // 다음 실행에 영향 주지 않게 원복.
            RangedProjectileVisualSystem.RenderingEnabled = true;
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[screenshotKey].wasPressedThisFrame)
            {
                CaptureScreenshot();
            }
        }

        private void CaptureScreenshot()
        {
            string dir = ScreenshotDirectory();
            Directory.CreateDirectory(dir);

            string file = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            string path = Path.Combine(dir, file);

            ScreenCapture.CaptureScreenshot(path, Mathf.Max(1, screenshotSuperSize));
            Debug.Log($"[TestToolbox] 스크린샷 저장: {path}");
        }

        private static string ScreenshotDirectory()
        {
#if UNITY_EDITOR
            // 에디터: 프로젝트 루트의 Screenshots 폴더 (Assets 상위).
            return Path.Combine(Application.dataPath, "..", "Screenshots");
#else
            return Path.Combine(Application.persistentDataPath, "Screenshots");
#endif
        }
    }
}
