using UnityEditor;
using UnityEngine;

namespace Swarm.EditorTools
{
    /// <summary>
    /// <see cref="TestSceneToolbox"/> 를 현재 씬에 추가하는 메뉴.
    /// </summary>
    public static class TestSceneToolboxMenu
    {
        [MenuItem("Tools/Test_Scene Toolbox 추가")]
        public static void AddToOpenScene()
        {
            var existing = Object.FindFirstObjectByType<TestSceneToolbox>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing);
                Debug.Log("[TestToolbox] 이미 씬에 TestSceneToolbox 가 있습니다.");
                return;
            }

            var go = new GameObject("[TestSceneToolbox]");
            go.AddComponent<TestSceneToolbox>();
            Undo.RegisterCreatedObjectUndo(go, "Add Test_Scene Toolbox");
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            Debug.Log("[TestToolbox] 추가됨. F12 로 스크린샷, 투사체는 기본 OFF.");
        }
    }
}
