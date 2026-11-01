using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lmbox.AIAgent.Tools
{
    internal static class ToolUtil
    {
        public static string GetPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }

        public static Transform FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var go = GameObject.Find(path);
            return go != null ? go.transform : null;
        }
    }

    /// <summary>Reads the active scene hierarchy as an indented text tree.</summary>
    public class GetSceneHierarchyTool : AgentTool
    {
        public override string Name => "get_scene_hierarchy";
        public override string Description =>
            "获取当前激活场景的 GameObject 层级树，包含每个节点的激活状态和组件列表。在修改场景前先调用它以了解现状。";

        public override string ParametersSchemaJson => @"{
            ""type"": ""object"",
            ""properties"": {
                ""maxDepth"": { ""type"": ""integer"", ""description"": ""最大遍历深度，默认 4"" }
            }
        }";

        public override Task<string> ExecuteAsync(JObject args)
        {
            int maxDepth = args?["maxDepth"]?.Value<int?>() ?? 4;
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
                return Task.FromResult("error: 没有激活的场景");

            var sb = new StringBuilder();
            sb.AppendLine($"场景: {scene.name} (路径: {scene.path})");
            foreach (var root in scene.GetRootGameObjects())
                AppendNode(root.transform, 0, maxDepth, sb);

            var text = sb.ToString();
            if (text.Length > 6000)
                text = text.Substring(0, 6000) + "\n...(已截断，请用更小的 maxDepth 分段查看)";
            return Task.FromResult(text);
        }

        private static void AppendNode(Transform t, int depth, int maxDepth, StringBuilder sb)
        {
            if (depth > maxDepth) return;
            var components = t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name);
            var compText = string.Join(",", components);
            sb.Append(' ', depth * 2)
              .Append(t.gameObject.activeSelf ? "● " : "○ ")
              .Append(t.name);
            if (compText.Length > 0)
                sb.Append("  [").Append(compText).Append(']');
            sb.AppendLine();

            foreach (Transform child in t)
                AppendNode(child, depth + 1, maxDepth, sb);
        }
    }

    /// <summary>Creates a GameObject, optionally a primitive, under an optional parent path.</summary>
    public class CreateGameObjectTool : AgentTool
    {
        public override string Name => "create_gameobject";
        public override string Description =>
            "在场景中创建一个 GameObject。可指定父节点路径、基础几何体类型和位置。创建后可被撤销。";

        public override string ParametersSchemaJson => @"{
            ""type"": ""object"",
            ""properties"": {
                ""name"": { ""type"": ""string"", ""description"": ""GameObject 名称"" },
                ""parent"": { ""type"": ""string"", ""description"": ""父节点层级路径，如 'Canvas/Panel'，留空则为根节点"" },
                ""primitiveType"": { ""type"": ""string"", ""enum"": [""Cube"",""Sphere"",""Capsule"",""Cylinder"",""Plane"",""Quad""], ""description"": ""可选，创建 Unity 基础几何体"" },
                ""position"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""x"": { ""type"": ""number"" },
                        ""y"": { ""type"": ""number"" },
                        ""z"": { ""type"": ""number"" }
                    }
                }
            },
            ""required"": [""name""]
        }";

        public override Task<string> ExecuteAsync(JObject args)
        {
            var name = args?["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("缺少参数 name");

            GameObject go;
            var primitive = args?["primitiveType"]?.ToString();
            if (!string.IsNullOrEmpty(primitive) &&
                Enum.TryParse(primitive, true, out PrimitiveType pt))
            {
                go = GameObject.CreatePrimitive(pt);
                go.name = name;
            }
            else
            {
                go = new GameObject(name);
            }

            var parentPath = args?["parent"]?.ToString();
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = ToolUtil.FindByPath(parentPath);
                if (parent == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    throw new ArgumentException($"找不到父节点: {parentPath}");
                }
                go.transform.SetParent(parent, false);
            }

            var pos = args?["position"] as JObject;
            if (pos != null)
            {
                go.transform.position = new Vector3(
                    pos["x"]?.Value<float>() ?? 0f,
                    pos["y"]?.Value<float>() ?? 0f,
                    pos["z"]?.Value<float>() ?? 0f);
            }

            Undo.RegisterCreatedObjectUndo(go, "AI Create GameObject");
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;

            return Task.FromResult($"已创建: {ToolUtil.GetPath(go.transform)}");
        }
    }

    /// <summary>Reads recent Unity Console entries captured by ConsoleLogCapture.</summary>
    public class ReadConsoleLogsTool : AgentTool
    {
        public override string Name => "read_console_logs";
        public override string Description =>
            "读取 Unity Console 最近的日志（含编译错误和运行时异常）。排查问题时优先使用。";

        public override string ParametersSchemaJson => @"{
            ""type"": ""object"",
            ""properties"": {
                ""count"": { ""type"": ""integer"", ""description"": ""返回条数，默认 20"" },
                ""type"": { ""type"": ""string"", ""enum"": [""Log"",""Warning"",""Error"",""Exception""], ""description"": ""按类型过滤，留空返回全部"" }
            }
        }";

        public override Task<string> ExecuteAsync(JObject args)
        {
            int count = args?["count"]?.Value<int?>() ?? 20;
            var type = args?["type"]?.ToString();
            return Task.FromResult(ConsoleLogCapture.DumpRecent(count, type));
        }
    }

    /// <summary>Writes (creates or overwrites) a text asset such as a C# script. Requires confirmation.</summary>
    public class WriteScriptTool : AgentTool
    {
        public override string Name => "write_script";
        public override string Description =>
            "创建或覆盖一个文本资源（通常是 .cs 脚本）。路径必须在 Assets/ 下。写入后 Unity 会自动编译，编译错误可用 read_console_logs 查看。此操作会覆盖同名文件。";

        public override bool RequiresConfirmation => true;

        public override string ParametersSchemaJson => @"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""资源路径，必须以 Assets/ 开头，如 Assets/Scripts/Player.cs"" },
                ""content"": { ""type"": ""string"", ""description"": ""完整文件内容"" }
            },
            ""required"": [""path"", ""content""]
        }";

        public override async Task<string> ExecuteAsync(JObject args)
        {
            var path = args?["path"]?.ToString();
            var content = args?["content"]?.ToString();
            if (string.IsNullOrEmpty(path) || content == null)
                throw new ArgumentException("缺少参数 path 或 content");
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("path 必须在 Assets/ 目录下");

            var assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("path 必须在 Assets/ 目录下，不能通过 .. 越界");
            // Reject linked ancestors/files as well as lexical traversal before writing.
            for (var current = fullPath; current != null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("path 不能包含符号链接");
                }
                catch (FileNotFoundException) { /* A new asset is allowed. */ }
                catch (DirectoryNotFoundException) { /* New directories are allowed. */ }
                if (current == assetsRoot) break;
            }
            path = "Assets/" + fullPath.Substring(assetsRoot.Length + 1).Replace('\\', '/');

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            bool existed = File.Exists(path);
            await File.WriteAllTextAsync(path, content);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return (existed ? "已覆盖: " : "已创建: ") + path +
                   "（Unity 正在编译，如有错误请调用 read_console_logs 查看）";
        }
    }

    /// <summary>Lists assets under a folder via AssetDatabase search.</summary>
    public class ListAssetsTool : AgentTool
    {
        public override string Name => "list_assets";
        public override string Description =>
            "列出项目中的资源文件。可用 AssetDatabase 搜索语法过滤，如 \"t:Script\"、\"t:Prefab Enemy\"。";

        public override string ParametersSchemaJson => @"{
            ""type"": ""object"",
            ""properties"": {
                ""folder"": { ""type"": ""string"", ""description"": ""搜索目录，默认 Assets"" },
                ""filter"": { ""type"": ""string"", ""description"": ""搜索过滤条件，如 t:Prefab，留空列出全部"" },
                ""limit"": { ""type"": ""integer"", ""description"": ""最多返回条数，默认 50"" }
            }
        }";

        public override Task<string> ExecuteAsync(JObject args)
        {
            var folder = args?["folder"]?.ToString();
            var filter = args?["filter"]?.ToString() ?? "";
            int limit = args?["limit"]?.Value<int?>() ?? 50;

            var folders = string.IsNullOrEmpty(folder) ? null : new[] { folder };
            var guids = AssetDatabase.FindAssets(filter, folders);
            var sb = new StringBuilder();
            sb.AppendLine($"共找到 {guids.Length} 个资源" + (guids.Length > limit ? $"，显示前 {limit} 个:" : ":"));
            foreach (var guid in guids.Take(limit))
                sb.AppendLine(AssetDatabase.GUIDToAssetPath(guid));

            return Task.FromResult(sb.ToString());
        }
    }

    /// <summary>Controls editor play mode.</summary>
    public class PlayModeControlTool : AgentTool
    {
        public override string Name => "play_mode_control";
        public override string Description =>
            "控制 Unity 编辑器播放模式：play 进入/继续，pause 暂停，stop 停止，step 单步推进一帧。";

        public override string ParametersSchemaJson => @"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""play"",""pause"",""unpause"",""stop"",""step""] }
            },
            ""required"": [""action""]
        }";

        public override Task<string> ExecuteAsync(JObject args)
        {
            var action = args?["action"]?.ToString()?.ToLowerInvariant();
            switch (action)
            {
                case "play":
                    EditorApplication.isPaused = false;
                    EditorApplication.isPlaying = true;
                    break;
                case "pause":
                    EditorApplication.isPaused = true;
                    break;
                case "unpause":
                    EditorApplication.isPaused = false;
                    break;
                case "stop":
                    EditorApplication.isPlaying = false;
                    break;
                case "step":
                    EditorApplication.Step();
                    break;
                default:
                    throw new ArgumentException($"未知 action: {action}");
            }
            return Task.FromResult($"已执行 {action}。当前: isPlaying={EditorApplication.isPlaying}, isPaused={EditorApplication.isPaused}");
        }
    }

    /// <summary>Executes an arbitrary editor menu item. Powerful but risky — requires confirmation.</summary>
    public class ExecuteMenuItemTool : AgentTool
    {
        public override string Name => "execute_menu_item";
        public override string Description =>
            "执行 Unity 编辑器菜单命令，如 \"Assets/Refresh\" 或 \"File/Save\"。仅在明确知道菜单路径时使用。";

        public override bool RequiresConfirmation => true;

        public override string ParametersSchemaJson => @"{
            ""type"": ""object"",
            ""properties"": {
                ""menuPath"": { ""type"": ""string"", ""description"": ""完整菜单路径，如 'File/Save Project'"" }
            },
            ""required"": [""menuPath""]
        }";

        public override Task<string> ExecuteAsync(JObject args)
        {
            var menuPath = args?["menuPath"]?.ToString();
            if (string.IsNullOrEmpty(menuPath))
                throw new ArgumentException("缺少参数 menuPath");

            bool ok = EditorApplication.ExecuteMenuItem(menuPath);
            return Task.FromResult(ok ? $"已执行菜单: {menuPath}" : $"菜单不存在或执行失败: {menuPath}");
        }
    }
}
