using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    public static class MCPSceneCommands
    {
        public static object GetSceneInfo()
        {
            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                var rootObjects = new List<string>();
                foreach (var go in scene.GetRootGameObjects())
                    rootObjects.Add(go.name);

                scenes.Add(new Dictionary<string, object>
                {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "isDirty", scene.isDirty },
                    { "isLoaded", scene.isLoaded },
                    { "rootObjectCount", scene.rootCount },
                    { "rootObjects", rootObjects },
                    { "buildIndex", scene.buildIndex },
                });
            }

            return new Dictionary<string, object>
            {
                { "activeScene", SceneManager.GetActiveScene().name },
                { "sceneCount", SceneManager.sceneCount },
                { "scenes", scenes },
            };
        }

        public static object OpenScene(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            // Auto-save unsaved changes instead of showing modal dialog
            if (SceneManager.GetActiveScene().isDirty)
            {
                var saveResult = SaveScene();
                var saveDict = saveResult as Dictionary<string, object>;
                if (saveDict == null || !(bool)saveDict["success"])
                {
                    return new { error = "Current scene has unsaved changes and auto-save failed", saveResult };
                }
            }

            var openedScene = EditorSceneManager.OpenScene(path);
            return new { success = true, name = openedScene.name, path = openedScene.path };
        }

        public static object SaveScene()
        {
            var scene = SceneManager.GetActiveScene();
            bool saved;
            if (string.IsNullOrEmpty(scene.path))
            {
                // Unnamed scene — auto-generate a path under Assets/Scenes/ to avoid
                // the modal "Save As" dialog that would block the MCP bridge.
                string dir = "Assets/Scenes";
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                string defaultPath = System.IO.Path.Combine(dir, "NewScene.unity");
                // Avoid overwriting an existing file — append a number if needed
                int suffix = 1;
                while (System.IO.File.Exists(defaultPath))
                    defaultPath = System.IO.Path.Combine(dir, $"NewScene_{suffix++}.unity");
                saved = EditorSceneManager.SaveScene(scene, defaultPath);
            }
            else
            {
                saved = EditorSceneManager.SaveScene(scene);
            }
            return new { success = saved, name = scene.name, path = scene.path };
        }

        public static object SaveSceneAs(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!path.EndsWith(".unity"))
                path += ".unity";

            // Ensure parent directory exists
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var scene = SceneManager.GetActiveScene();
            bool saved = EditorSceneManager.SaveScene(scene, path);
            return new { success = saved, name = scene.name, path = scene.path };
        }

        public static object NewScene()
        {
            // Auto-save current scene to avoid modal "Save changes?" dialog
            if (SceneManager.GetActiveScene().isDirty)
            {
                var saveResult = SaveScene();
                var saveDict = saveResult as Dictionary<string, object>;
                if (saveDict == null || !(bool)saveDict["success"])
                {
                    return new { error = "Current scene has unsaved changes and auto-save failed", saveResult };
                }
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            return new { success = true, name = scene.name };
        }

        public static object GetHierarchy(Dictionary<string, object> args)
        {
            int maxDepth = 10;
            if (args != null && args.ContainsKey("maxDepth"))
                maxDepth = System.Convert.ToInt32(args["maxDepth"]);

            // Max total nodes to return — prevents Write EOF on large scenes (79K+ objects)
            int maxNodes = 5000;
            if (args != null && args.ContainsKey("maxNodes"))
                maxNodes = System.Convert.ToInt32(args["maxNodes"]);

            // Optional: only return hierarchy under a specific parent path
            string parentPath = null;
            if (args != null && args.ContainsKey("parentPath"))
                parentPath = args["parentPath"]?.ToString();

            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();

            // Count total objects in scene for metadata
            int totalSceneObjects = CountAllObjects(rootObjects);

            // If parentPath specified, find that object and only return its subtree
            GameObject[] startObjects = rootObjects;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var found = GameObject.Find(parentPath);
                if (found == null)
                    return new Dictionary<string, object>
                    {
                        { "error", $"GameObject not found at path: {parentPath}" },
                    };
                startObjects = new[] { found };
            }

            int nodeCount = 0;
            var hierarchy = new List<object>();

            foreach (var root in startObjects)
            {
                var node = BuildHierarchyNode(root, 0, maxDepth, ref nodeCount, maxNodes);
                if (node != null)
                    hierarchy.Add(node);
                if (nodeCount >= maxNodes)
                    break;
            }

            var result = new Dictionary<string, object>
            {
                { "scene", scene.name },
                { "hierarchy", hierarchy },
                { "totalSceneObjects", totalSceneObjects },
                { "returnedNodes", nodeCount },
                { "maxNodes", maxNodes },
            };

            if (nodeCount >= maxNodes)
            {
                result["truncated"] = true;
                result["message"] = $"Hierarchy truncated at {maxNodes} nodes (scene has {totalSceneObjects} total objects). " +
                    "Use parentPath to explore specific subtrees, or increase maxNodes.";
            }

            if (!string.IsNullOrEmpty(parentPath))
                result["parentPath"] = parentPath;

            return result;
        }

        /// <summary>Count all GameObjects recursively without building the full hierarchy.</summary>
        private static int CountAllObjects(GameObject[] roots)
        {
            int count = 0;
            foreach (var root in roots)
                CountRecursive(root, ref count);
            return count;
        }

        private static void CountRecursive(GameObject go, ref int count)
        {
            count++;
            foreach (Transform child in go.transform)
                CountRecursive(child.gameObject, ref count);
        }

        private static Dictionary<string, object> BuildHierarchyNode(
            GameObject go, int depth, int maxDepth, ref int nodeCount, int maxNodes)
        {
            if (nodeCount >= maxNodes)
                return null;

            nodeCount++;

            var components = new List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    components.Add(comp.GetType().Name);
            }

            var node = new Dictionary<string, object>
            {
                { "name", go.name },
                { "instanceId", go.GetInstanceID() },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "components", components },
                { "position", VectorToDict(go.transform.position) },
            };

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (nodeCount >= maxNodes)
                    {
                        // Record how many children we couldn't include
                        node["childCount"] = go.transform.childCount;
                        node["childrenIncluded"] = children.Count;
                        node["childrenTruncated"] = true;
                        break;
                    }
                    var childNode = BuildHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth, ref nodeCount, maxNodes);
                    if (childNode != null)
                        children.Add(childNode);
                }
                if (children.Count > 0)
                    node["children"] = children;
                if (!node.ContainsKey("childCount"))
                    node["childCount"] = go.transform.childCount;
            }
            else if (go.transform.childCount > 0)
            {
                node["childCount"] = go.transform.childCount;
                node["childrenTruncated"] = true;
            }

            return node;
        }

        private static Dictionary<string, object> VectorToDict(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }
    }
}
