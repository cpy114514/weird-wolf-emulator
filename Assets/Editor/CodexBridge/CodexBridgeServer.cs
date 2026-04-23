using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CodexBridge
{
    [InitializeOnLoad]
    internal static class CodexBridgeServer
    {
        private const int Port = 17379;
        private const string AutoStartKey = "CodexBridge.AutoStart";
        private const string TokenDirectory = "Library/CodexBridge";
        private const string TokenPath = TokenDirectory + "/token.txt";

        private static readonly object QueueLock = new object();
        private static readonly Queue<BridgeJob> Jobs = new Queue<BridgeJob>();

        private static TcpListener listener;
        private static Thread listenerThread;
        private static volatile bool running;
        private static string token;

        static CodexBridgeServer()
        {
            EditorApplication.update += Pump;

            if (!EditorPrefs.HasKey(AutoStartKey))
            {
                EditorPrefs.SetBool(AutoStartKey, true);
            }

            if (EditorPrefs.GetBool(AutoStartKey, true))
            {
                StartServer();
            }
        }

        [MenuItem("Tools/Codex Bridge/Start Server")]
        private static void StartServerMenu()
        {
            EditorPrefs.SetBool(AutoStartKey, true);
            StartServer();
        }

        [MenuItem("Tools/Codex Bridge/Stop Server")]
        private static void StopServerMenu()
        {
            EditorPrefs.SetBool(AutoStartKey, false);
            StopServer();
        }

        [MenuItem("Tools/Codex Bridge/Print Status")]
        private static void PrintStatus()
        {
            Debug.Log(running
                ? "Codex Bridge is running on http://127.0.0.1:" + Port + "/"
                : "Codex Bridge is stopped.");
        }

        private static void StartServer()
        {
            if (running)
            {
                return;
            }

            try
            {
                token = EnsureToken();
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                running = true;
                listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "Codex Bridge Listener"
                };
                listenerThread.Start();
                Debug.Log("Codex Bridge listening on http://127.0.0.1:" + Port + "/");
            }
            catch (Exception ex)
            {
                running = false;
                Debug.LogError("Codex Bridge failed to start: " + ex.Message);
            }
        }

        private static void StopServer()
        {
            running = false;
            try
            {
                listener?.Stop();
            }
            catch
            {
                // Ignore shutdown races from the listener thread.
            }

            listener = null;
        }

        private static string EnsureToken()
        {
            if (!Directory.Exists(TokenDirectory))
            {
                Directory.CreateDirectory(TokenDirectory);
            }

            if (File.Exists(TokenPath))
            {
                var existing = File.ReadAllText(TokenPath).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    return existing;
                }
            }

            var generated = Guid.NewGuid().ToString("N");
            File.WriteAllText(TokenPath, generated);
            return generated;
        }

        private static void ListenLoop()
        {
            while (running)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch
                {
                    if (running)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;

                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true))
                    {
                        var requestLine = reader.ReadLine();
                        if (string.IsNullOrEmpty(requestLine))
                        {
                            return;
                        }

                        var pieces = requestLine.Split(' ');
                        if (pieces.Length < 2)
                        {
                            WriteResponse(stream, 400, "{\"ok\":false,\"error\":\"Bad request.\"}");
                            return;
                        }

                        var method = pieces[0];
                        var target = pieces[1];
                        var headers = ReadHeaders(reader);
                        var body = ReadBody(reader, headers);

                        if (!IsAuthorized(headers))
                        {
                            WriteResponse(stream, 401, "{\"ok\":false,\"error\":\"Missing or invalid X-Codex-Bridge-Token.\"}");
                            return;
                        }

                        var job = new BridgeJob(method, target, body);
                        lock (QueueLock)
                        {
                            Jobs.Enqueue(job);
                        }

                        job.WaitHandle.WaitOne();
                        WriteResponse(stream, job.StatusCode, job.ResponseJson);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        var stream = client.GetStream();
                        WriteResponse(stream, 500, "{\"ok\":false,\"error\":\"" + Escape(ex.Message) + "\"}");
                    }
                    catch
                    {
                        // The client might already be gone.
                    }
                }
            }
        }

        private static Dictionary<string, string> ReadHeaders(StreamReader reader)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var split = line.IndexOf(':');
                if (split <= 0)
                {
                    continue;
                }

                headers[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
            }

            return headers;
        }

        private static string ReadBody(StreamReader reader, Dictionary<string, string> headers)
        {
            if (!headers.TryGetValue("Content-Length", out var lengthText))
            {
                return string.Empty;
            }

            if (!int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length <= 0)
            {
                return string.Empty;
            }

            var buffer = new char[length];
            var total = 0;
            while (total < length)
            {
                var read = reader.Read(buffer, total, length - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return new string(buffer, 0, total);
        }

        private static bool IsAuthorized(Dictionary<string, string> headers)
        {
            if (string.IsNullOrEmpty(token))
            {
                token = EnsureToken();
            }

            return headers.TryGetValue("X-Codex-Bridge-Token", out var supplied) && supplied == token;
        }

        private static void WriteResponse(NetworkStream stream, int statusCode, string json)
        {
            var reason = statusCode == 200 ? "OK" :
                statusCode == 401 ? "Unauthorized" :
                statusCode == 404 ? "Not Found" :
                statusCode == 400 ? "Bad Request" : "Internal Server Error";
            var body = Encoding.UTF8.GetBytes(json);
            var header =
                "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Access-Control-Allow-Origin: none\r\n" +
                "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        private static void Pump()
        {
            BridgeJob job = null;
            lock (QueueLock)
            {
                if (Jobs.Count > 0)
                {
                    job = Jobs.Dequeue();
                }
            }

            if (job == null)
            {
                return;
            }

            try
            {
                job.ResponseJson = Dispatch(job.Method, job.Target, job.Body, out job.StatusCode);
            }
            catch (Exception ex)
            {
                job.StatusCode = 500;
                job.ResponseJson = "{\"ok\":false,\"error\":\"" + Escape(ex.Message) + "\"}";
            }
            finally
            {
                job.WaitHandle.Set();
            }
        }

        private static string Dispatch(string method, string target, string body, out int statusCode)
        {
            statusCode = 200;
            var route = target.Split('?')[0].TrimEnd('/');
            if (route.Length == 0)
            {
                route = "/";
            }

            if (method == "GET" && route == "/status")
            {
                return StatusJson();
            }

            if (method == "GET" && route == "/hierarchy")
            {
                return HierarchyJson();
            }

            if (method == "POST" && route == "/select")
            {
                var request = JsonUtility.FromJson<PathRequest>(body);
                return SelectJson(request);
            }

            if (method == "POST" && route == "/set-transform")
            {
                var request = JsonUtility.FromJson<TransformRequest>(body);
                return SetTransformJson(request);
            }

            if (method == "POST" && route == "/fit-root-box-collider2d")
            {
                var request = JsonUtility.FromJson<FitRootBoxCollider2DRequest>(body);
                return FitRootBoxCollider2DJson(request);
            }

            if (method == "POST" && route == "/fit-piece-box-collider2d")
            {
                var request = JsonUtility.FromJson<FitPieceBoxCollider2DRequest>(body);
                return FitPieceBoxCollider2DJson(request);
            }

            if (method == "POST" && route == "/remove-box-collider2d")
            {
                var request = JsonUtility.FromJson<PathRequest>(body);
                return RemoveBoxCollider2DJson(request);
            }

            if (method == "POST" && route == "/setup-simple-wolf-controller")
            {
                var request = JsonUtility.FromJson<MovementSetupRequest>(body);
                return SetupSimpleWolfControllerJson(request);
            }

            if (method == "POST" && route == "/smooth-animation-clip")
            {
                var request = JsonUtility.FromJson<AnimationClipRequest>(body);
                return SmoothAnimationClipJson(request);
            }

            if (method == "POST" && route == "/create-idle-from-current-pose")
            {
                var request = JsonUtility.FromJson<IdleClipRequest>(body);
                return CreateIdleFromCurrentPoseJson(request);
            }

            if (method == "POST" && route == "/save-scene")
            {
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                return "{\"ok\":true,\"saved\":true}";
            }

            statusCode = 404;
            return "{\"ok\":false,\"error\":\"Unknown endpoint.\"}";
        }

        private static string StatusJson()
        {
            var scene = SceneManager.GetActiveScene();
            var selected = Selection.activeGameObject != null ? GetPath(Selection.activeGameObject.transform) : "";
            return "{" +
                   "\"ok\":true," +
                   "\"unityVersion\":\"" + Escape(Application.unityVersion) + "\"," +
                   "\"activeScene\":\"" + Escape(scene.path) + "\"," +
                   "\"sceneName\":\"" + Escape(scene.name) + "\"," +
                   "\"sceneDirty\":" + (scene.isDirty ? "true" : "false") + "," +
                   "\"isPlaying\":" + (EditorApplication.isPlaying ? "true" : "false") + "," +
                   "\"isCompiling\":" + (EditorApplication.isCompiling ? "true" : "false") + "," +
                   "\"selected\":\"" + Escape(selected) + "\"" +
                   "}";
        }

        private static string HierarchyJson()
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var builder = new StringBuilder();
            builder.Append("{\"ok\":true,\"scene\":\"").Append(Escape(scene.path)).Append("\",\"objects\":[");
            var first = true;
            foreach (var root in roots)
            {
                AppendObjectAndChildren(builder, root.transform, ref first);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private static void AppendObjectAndChildren(StringBuilder builder, Transform transform, ref bool first)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            var go = transform.gameObject;
            builder.Append('{');
            builder.Append("\"path\":\"").Append(Escape(GetPath(transform))).Append("\",");
            builder.Append("\"name\":\"").Append(Escape(go.name)).Append("\",");
            builder.Append("\"activeSelf\":").Append(go.activeSelf ? "true" : "false").Append(',');
            builder.Append("\"activeInHierarchy\":").Append(go.activeInHierarchy ? "true" : "false").Append(',');
            AppendVector(builder, "localPosition", transform.localPosition);
            builder.Append(',');
            AppendVector(builder, "localEulerAngles", transform.localEulerAngles);
            builder.Append(',');
            AppendVector(builder, "localScale", transform.localScale);
            builder.Append(',');
            AppendComponents(builder, go);
            builder.Append('}');

            for (var i = 0; i < transform.childCount; i++)
            {
                AppendObjectAndChildren(builder, transform.GetChild(i), ref first);
            }
        }

        private static void AppendComponents(StringBuilder builder, GameObject go)
        {
            builder.Append("\"components\":[");
            var components = go.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var component = components[i];
                builder.Append('{');
                builder.Append("\"type\":\"").Append(Escape(component != null ? component.GetType().FullName : "Missing")).Append("\"");

                var renderer = component as SpriteRenderer;
                if (renderer != null)
                {
                    builder.Append(",\"sprite\":\"").Append(Escape(renderer.sprite != null ? renderer.sprite.name : "")).Append("\"");
                    builder.Append(",\"sortingLayer\":\"").Append(Escape(renderer.sortingLayerName)).Append("\"");
                    builder.Append(",\"sortingOrder\":").Append(renderer.sortingOrder.ToString(CultureInfo.InvariantCulture));
                }

                builder.Append('}');
            }

            builder.Append(']');
        }

        private static string SelectJson(PathRequest request)
        {
            var transform = FindByPath(request != null ? request.path : "");
            if (transform == null)
            {
                return "{\"ok\":false,\"error\":\"GameObject path not found.\"}";
            }

            Selection.activeTransform = transform;
            EditorGUIUtility.PingObject(transform.gameObject);
            return "{\"ok\":true,\"selected\":\"" + Escape(GetPath(transform)) + "\"}";
        }

        private static string SetTransformJson(TransformRequest request)
        {
            if (request == null)
            {
                return "{\"ok\":false,\"error\":\"Missing JSON body.\"}";
            }

            var transform = FindByPath(request.path);
            if (transform == null)
            {
                return "{\"ok\":false,\"error\":\"GameObject path not found.\"}";
            }

            Undo.RecordObject(transform, "Codex Bridge Set Transform");

            if (request.hasLocalPosition)
            {
                transform.localPosition = request.localPosition.ToVector3();
            }

            if (request.hasLocalEulerAngles)
            {
                transform.localEulerAngles = request.localEulerAngles.ToVector3();
            }

            if (request.hasLocalScale)
            {
                transform.localScale = request.localScale.ToVector3();
            }

            EditorUtility.SetDirty(transform);
            EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
            return "{\"ok\":true,\"path\":\"" + Escape(GetPath(transform)) + "\"}";
        }

        private static string FitRootBoxCollider2DJson(FitRootBoxCollider2DRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.rootPath))
            {
                return "{\"ok\":false,\"error\":\"Missing rootPath.\"}";
            }

            var root = FindByPath(request.rootPath);
            if (root == null)
            {
                return "{\"ok\":false,\"error\":\"Root GameObject path not found.\"}";
            }

            var renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
            var hasBounds = false;
            var bounds = new Bounds();
            var rendererCount = 0;

            foreach (var spriteRenderer in renderers)
            {
                if (spriteRenderer == null || spriteRenderer.sprite == null)
                {
                    continue;
                }

                if (!request.includeDisabledRenderers && !spriteRenderer.enabled)
                {
                    continue;
                }

                rendererCount++;
                EncapsulateRendererBounds(root, spriteRenderer, ref bounds, ref hasBounds);
            }

            if (!hasBounds)
            {
                return "{\"ok\":false,\"error\":\"No SpriteRenderer bounds found under rootPath.\"}";
            }

            var collider = root.GetComponent<BoxCollider2D>();
            if (collider == null)
            {
                collider = Undo.AddComponent<BoxCollider2D>(root.gameObject);
            }
            else
            {
                Undo.RecordObject(collider, "Codex Bridge Fit Root BoxCollider2D");
            }

            var padding = Mathf.Max(0f, request.padding);
            collider.offset = new Vector2(bounds.center.x, bounds.center.y);
            collider.size = new Vector2(
                Mathf.Max(0.001f, bounds.size.x + padding * 2f),
                Mathf.Max(0.001f, bounds.size.y + padding * 2f));
            collider.edgeRadius = 0f;
            collider.enabled = true;
            EditorUtility.SetDirty(collider);

            var disabledCount = 0;
            if (request.disableChildBoxColliders)
            {
                foreach (var childCollider in root.GetComponentsInChildren<BoxCollider2D>(true))
                {
                    if (childCollider == null || childCollider == collider)
                    {
                        continue;
                    }

                    Undo.RecordObject(childCollider, "Codex Bridge Disable Child BoxCollider2D");
                    childCollider.enabled = false;
                    EditorUtility.SetDirty(childCollider);
                    disabledCount++;
                }
            }

            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);

            return "{" +
                   "\"ok\":true," +
                   "\"path\":\"" + Escape(GetPath(root)) + "\"," +
                   "\"rendererCount\":" + rendererCount.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"disabledChildColliders\":" + disabledCount.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"offset\":{\"x\":" + Float(collider.offset.x) + ",\"y\":" + Float(collider.offset.y) + "}," +
                   "\"size\":{\"x\":" + Float(collider.size.x) + ",\"y\":" + Float(collider.size.y) + "}" +
                   "}";
        }

        private static string FitPieceBoxCollider2DJson(FitPieceBoxCollider2DRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.rootPath))
            {
                return "{\"ok\":false,\"error\":\"Missing rootPath.\"}";
            }

            var root = FindByPath(request.rootPath);
            if (root == null)
            {
                return "{\"ok\":false,\"error\":\"Root GameObject path not found.\"}";
            }

            var rootCollider = root.GetComponent<BoxCollider2D>();
            var disabledRootCollider = false;
            var removedRootCollider = false;
            if (request.removeRootBoxCollider && rootCollider != null)
            {
                Undo.DestroyObjectImmediate(rootCollider);
                removedRootCollider = true;
            }
            else if (request.disableRootBoxCollider && rootCollider != null)
            {
                Undo.RecordObject(rootCollider, "Codex Bridge Disable Root BoxCollider2D");
                rootCollider.enabled = false;
                EditorUtility.SetDirty(rootCollider);
                disabledRootCollider = true;
            }

            var renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
            var fitted = 0;
            var builder = new StringBuilder();
            builder.Append("{\"ok\":true,\"disabledRootCollider\":").Append(disabledRootCollider ? "true" : "false");
            builder.Append(",\"removedRootCollider\":").Append(removedRootCollider ? "true" : "false");
            builder.Append(",\"pieces\":[");

            foreach (var spriteRenderer in renderers)
            {
                if (spriteRenderer == null || spriteRenderer.sprite == null)
                {
                    continue;
                }

                if (!request.includeDisabledRenderers && !spriteRenderer.enabled)
                {
                    continue;
                }

                var localBounds = GetBestLocalBounds(spriteRenderer, request.useSpriteBounds);
                var padding = Mathf.Max(0f, request.padding);
                var collider = spriteRenderer.GetComponent<BoxCollider2D>();
                if (collider == null)
                {
                    collider = Undo.AddComponent<BoxCollider2D>(spriteRenderer.gameObject);
                }
                else
                {
                    Undo.RecordObject(collider, "Codex Bridge Fit Piece BoxCollider2D");
                }

                collider.offset = new Vector2(localBounds.center.x, localBounds.center.y);
                collider.size = new Vector2(
                    Mathf.Max(0.001f, localBounds.size.x + padding * 2f),
                    Mathf.Max(0.001f, localBounds.size.y + padding * 2f));
                collider.edgeRadius = 0f;
                collider.enabled = true;
                EditorUtility.SetDirty(collider);

                if (fitted > 0)
                {
                    builder.Append(',');
                }

                builder.Append('{');
                builder.Append("\"path\":\"").Append(Escape(GetPath(spriteRenderer.transform))).Append("\",");
                builder.Append("\"offset\":{\"x\":").Append(Float(collider.offset.x)).Append(",\"y\":").Append(Float(collider.offset.y)).Append("},");
                builder.Append("\"size\":{\"x\":").Append(Float(collider.size.x)).Append(",\"y\":").Append(Float(collider.size.y)).Append("}");
                builder.Append('}');
                fitted++;
            }

            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);

            builder.Append("],\"fitted\":").Append(fitted.ToString(CultureInfo.InvariantCulture)).Append('}');
            return builder.ToString();
        }

        private static string RemoveBoxCollider2DJson(PathRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.path))
            {
                return "{\"ok\":false,\"error\":\"Missing path.\"}";
            }

            var root = FindByPath(request.path);
            if (root == null)
            {
                return "{\"ok\":false,\"error\":\"Root GameObject path not found.\"}";
            }

            var colliders = root.GetComponentsInChildren<BoxCollider2D>(true);
            var removed = 0;
            foreach (var collider in colliders)
            {
                if (collider == null)
                {
                    continue;
                }

                Undo.DestroyObjectImmediate(collider);
                removed++;
            }

            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            return "{\"ok\":true,\"path\":\"" + Escape(GetPath(root)) + "\",\"removed\":" + removed.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string SetupSimpleWolfControllerJson(MovementSetupRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.wolfPath))
            {
                return "{\"ok\":false,\"error\":\"Missing wolfPath.\"}";
            }

            var wolf = FindByPath(request.wolfPath);
            if (wolf == null)
            {
                return "{\"ok\":false,\"error\":\"Wolf GameObject path not found.\"}";
            }

            var ground = !string.IsNullOrEmpty(request.groundPath) ? FindByPath(request.groundPath) : null;
            if (ground == null)
            {
                return "{\"ok\":false,\"error\":\"Ground GameObject path not found.\"}";
            }

            var wolfCollider = wolf.GetComponent<Collider2D>();
            if (wolfCollider == null)
            {
                wolfCollider = Undo.AddComponent<BoxCollider2D>(wolf.gameObject);
            }

            var body = wolf.GetComponent<Rigidbody2D>();
            if (body == null)
            {
                body = Undo.AddComponent<Rigidbody2D>(wolf.gameObject);
            }
            else
            {
                Undo.RecordObject(body, "Codex Bridge Setup Simple Wolf Controller");
            }

            body.bodyType = RigidbodyType2D.Dynamic;
            body.simulated = true;
            body.gravityScale = request.gravityScale > 0f ? request.gravityScale : 2.5f;
            body.constraints = RigidbodyConstraints2D.FreezeRotation;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;
            EditorUtility.SetDirty(body);

            var groundCollider = ground.GetComponent<Collider2D>();
            var addedGroundCollider = false;
            if (groundCollider == null)
            {
                groundCollider = Undo.AddComponent<BoxCollider2D>(ground.gameObject);
                addedGroundCollider = true;
            }

            EditorUtility.SetDirty(groundCollider);

            var controllerType = Type.GetType("SimpleWolfController, Assembly-CSharp");
            if (controllerType == null)
            {
                return "{\"ok\":false,\"error\":\"SimpleWolfController type is not compiled yet.\"}";
            }

            var controller = wolf.GetComponent(controllerType);
            var addedController = false;
            if (controller == null)
            {
                controller = Undo.AddComponent(wolf.gameObject, controllerType);
                addedController = true;
            }

            var serialized = new SerializedObject(controller);
            var moveSpeed = serialized.FindProperty("moveSpeed");
            var jumpSpeed = serialized.FindProperty("jumpSpeed");
            var groundLayers = serialized.FindProperty("groundLayers");
            if (moveSpeed != null)
            {
                moveSpeed.floatValue = request.moveSpeed > 0f ? request.moveSpeed : 2.5f;
            }

            if (jumpSpeed != null)
            {
                jumpSpeed.floatValue = request.jumpSpeed > 0f ? request.jumpSpeed : 4.5f;
            }

            if (groundLayers != null)
            {
                groundLayers.intValue = -1;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(controller);

            EditorSceneManager.MarkSceneDirty(wolf.gameObject.scene);
            return "{" +
                   "\"ok\":true," +
                   "\"wolf\":\"" + Escape(GetPath(wolf)) + "\"," +
                   "\"ground\":\"" + Escape(GetPath(ground)) + "\"," +
                   "\"hasWolfCollider\":" + (wolfCollider != null ? "true" : "false") + "," +
                   "\"addedGroundCollider\":" + (addedGroundCollider ? "true" : "false") + "," +
                   "\"addedController\":" + (addedController ? "true" : "false") +
                   "}";
        }

        private static string SmoothAnimationClipJson(AnimationClipRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.assetPath))
            {
                return "{\"ok\":false,\"error\":\"Missing assetPath.\"}";
            }

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(request.assetPath);
            if (clip == null)
            {
                return "{\"ok\":false,\"error\":\"AnimationClip not found.\"}";
            }

            var bindings = AnimationUtility.GetCurveBindings(clip);
            var curveCount = 0;
            var keyCount = 0;
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0)
                {
                    continue;
                }

                for (var i = 0; i < curve.length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
                    AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
                    keyCount++;
                }

                AnimationUtility.SetEditorCurve(clip, binding, curve);
                curveCount++;
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            settings.loopBlend = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return "{\"ok\":true,\"assetPath\":\"" + Escape(request.assetPath) + "\",\"curves\":" +
                   curveCount.ToString(CultureInfo.InvariantCulture) + ",\"keys\":" +
                   keyCount.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string CreateIdleFromCurrentPoseJson(IdleClipRequest request)
        {
            if (request == null ||
                string.IsNullOrEmpty(request.rootPath) ||
                string.IsNullOrEmpty(request.walkClipPath) ||
                string.IsNullOrEmpty(request.idleClipPath) ||
                string.IsNullOrEmpty(request.controllerPath))
            {
                return "{\"ok\":false,\"error\":\"Missing rootPath, walkClipPath, idleClipPath, or controllerPath.\"}";
            }

            var root = FindByPath(request.rootPath);
            if (root == null)
            {
                return "{\"ok\":false,\"error\":\"Root GameObject path not found.\"}";
            }

            var walkClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(request.walkClipPath);
            if (walkClip == null)
            {
                return "{\"ok\":false,\"error\":\"Walk AnimationClip not found.\"}";
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(request.controllerPath);
            if (controller == null)
            {
                return "{\"ok\":false,\"error\":\"AnimatorController not found.\"}";
            }

            var idleClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(request.idleClipPath);
            if (idleClip == null)
            {
                idleClip = new AnimationClip
                {
                    frameRate = walkClip.frameRate > 0f ? walkClip.frameRate : 60f,
                    name = Path.GetFileNameWithoutExtension(request.idleClipPath)
                };
                AssetDatabase.CreateAsset(idleClip, request.idleClipPath);
            }
            else
            {
                ClearCurves(idleClip);
            }

            var duration = request.duration > 0f ? request.duration : 1f;
            var bindings = AnimationUtility.GetCurveBindings(walkClip);
            var curves = 0;
            var missing = 0;

            foreach (var binding in bindings)
            {
                var value = TryReadBindingValue(root, binding, out var found);
                if (!found)
                {
                    missing++;
                    continue;
                }

                var curve = AnimationCurve.Linear(0f, value, duration, value);
                AnimationUtility.SetKeyLeftTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, 1, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyRightTangentMode(curve, 1, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetEditorCurve(idleClip, binding, curve);
                curves++;
            }

            var settings = AnimationUtility.GetAnimationClipSettings(idleClip);
            settings.loopTime = true;
            settings.loopBlend = true;
            AnimationUtility.SetAnimationClipSettings(idleClip, settings);
            EditorUtility.SetDirty(idleClip);

            EnsureControllerState(controller, request.idleStateName, idleClip, new Vector3(200f, 120f, 0f), true);
            EnsureControllerState(controller, request.walkStateName, walkClip, new Vector3(200f, 0f, 0f), false);
            EditorUtility.SetDirty(controller);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return "{\"ok\":true,\"idleClip\":\"" + Escape(request.idleClipPath) + "\",\"curves\":" +
                   curves.ToString(CultureInfo.InvariantCulture) + ",\"missing\":" +
                   missing.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static void ClearCurves(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                AnimationUtility.SetEditorCurve(clip, binding, null);
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
            }
        }

        private static float TryReadBindingValue(Transform root, EditorCurveBinding binding, out bool found)
        {
            found = false;
            var target = string.IsNullOrEmpty(binding.path) ? root : root.Find(binding.path);
            if (target == null)
            {
                return 0f;
            }

            found = true;
            switch (binding.propertyName)
            {
                case "m_LocalPosition.x":
                case "localPosition.x":
                    return target.localPosition.x;
                case "m_LocalPosition.y":
                case "localPosition.y":
                    return target.localPosition.y;
                case "m_LocalPosition.z":
                case "localPosition.z":
                    return target.localPosition.z;
                case "m_LocalScale.x":
                case "localScale.x":
                    return target.localScale.x;
                case "m_LocalScale.y":
                case "localScale.y":
                    return target.localScale.y;
                case "m_LocalScale.z":
                case "localScale.z":
                    return target.localScale.z;
                case "localEulerAnglesRaw.x":
                    return NormalizeAngle(target.localEulerAngles.x);
                case "localEulerAnglesRaw.y":
                    return NormalizeAngle(target.localEulerAngles.y);
                case "localEulerAnglesRaw.z":
                    return NormalizeAngle(target.localEulerAngles.z);
                default:
                    found = false;
                    return 0f;
            }
        }

        private static float NormalizeAngle(float angle)
        {
            while (angle > 180f)
            {
                angle -= 360f;
            }

            while (angle < -180f)
            {
                angle += 360f;
            }

            return angle;
        }

        private static void EnsureControllerState(
            AnimatorController controller,
            string stateName,
            Motion motion,
            Vector3 position,
            bool makeDefault)
        {
            if (controller.layers == null || controller.layers.Length == 0)
            {
                controller.AddLayer("Base Layer");
            }

            var stateMachine = controller.layers[0].stateMachine;
            AnimatorState state = null;
            foreach (var childState in stateMachine.states)
            {
                if (childState.state != null && childState.state.name == stateName)
                {
                    state = childState.state;
                    break;
                }
            }

            if (state == null)
            {
                state = stateMachine.AddState(stateName, position);
            }

            state.motion = motion;
            state.speed = 1f;
            state.writeDefaultValues = true;

            if (makeDefault)
            {
                stateMachine.defaultState = state;
            }
        }

        private static Bounds GetBestLocalBounds(SpriteRenderer spriteRenderer, bool useSpriteBounds)
        {
            if (spriteRenderer.sprite != null && useSpriteBounds)
            {
                return spriteRenderer.sprite.bounds;
            }

            var localBounds = spriteRenderer.localBounds;
            if (IsUsableBounds(localBounds))
            {
                return localBounds;
            }

            return spriteRenderer.sprite != null ? spriteRenderer.sprite.bounds : new Bounds(Vector3.zero, Vector3.one * 0.01f);
        }

        private static bool IsUsableBounds(Bounds bounds)
        {
            return IsFinite(bounds.center.x) &&
                   IsFinite(bounds.center.y) &&
                   IsFinite(bounds.size.x) &&
                   IsFinite(bounds.size.y) &&
                   bounds.size.x > 0.0001f &&
                   bounds.size.y > 0.0001f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void EncapsulateRendererBounds(Transform root, Renderer renderer, ref Bounds bounds, ref bool hasBounds)
        {
            var rendererBounds = renderer.bounds;
            var min = rendererBounds.min;
            var max = rendererBounds.max;
            Encapsulate(root.InverseTransformPoint(new Vector3(min.x, min.y, min.z)), ref bounds, ref hasBounds);
            Encapsulate(root.InverseTransformPoint(new Vector3(min.x, max.y, min.z)), ref bounds, ref hasBounds);
            Encapsulate(root.InverseTransformPoint(new Vector3(max.x, min.y, min.z)), ref bounds, ref hasBounds);
            Encapsulate(root.InverseTransformPoint(new Vector3(max.x, max.y, min.z)), ref bounds, ref hasBounds);
            Encapsulate(root.InverseTransformPoint(new Vector3(min.x, min.y, max.z)), ref bounds, ref hasBounds);
            Encapsulate(root.InverseTransformPoint(new Vector3(min.x, max.y, max.z)), ref bounds, ref hasBounds);
            Encapsulate(root.InverseTransformPoint(new Vector3(max.x, min.y, max.z)), ref bounds, ref hasBounds);
            Encapsulate(root.InverseTransformPoint(new Vector3(max.x, max.y, max.z)), ref bounds, ref hasBounds);
        }

        private static void Encapsulate(Vector3 point, ref Bounds bounds, ref bool hasBounds)
        {
            if (!hasBounds)
            {
                bounds = new Bounds(point, Vector3.zero);
                hasBounds = true;
                return;
            }

            bounds.Encapsulate(point);
        }

        private static Transform FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == path)
                {
                    return root.transform;
                }

                if (path.StartsWith(root.name + "/", StringComparison.Ordinal))
                {
                    return root.transform.Find(path.Substring(root.name.Length + 1));
                }
            }

            return null;
        }

        private static string GetPath(Transform transform)
        {
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }

            return string.Join("/", names.ToArray());
        }

        private static void AppendVector(StringBuilder builder, string name, Vector3 vector)
        {
            builder.Append('"').Append(name).Append("\":{");
            builder.Append("\"x\":").Append(Float(vector.x)).Append(',');
            builder.Append("\"y\":").Append(Float(vector.y)).Append(',');
            builder.Append("\"z\":").Append(Float(vector.z)).Append('}');
        }

        private static string Float(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        [Serializable]
        private sealed class PathRequest
        {
            public string path;
        }

        [Serializable]
        private sealed class TransformRequest
        {
            public string path;
            public bool hasLocalPosition;
            public BridgeVector3 localPosition;
            public bool hasLocalEulerAngles;
            public BridgeVector3 localEulerAngles;
            public bool hasLocalScale;
            public BridgeVector3 localScale;
        }

        [Serializable]
        private sealed class FitRootBoxCollider2DRequest
        {
            public string rootPath;
            public float padding;
            public bool includeDisabledRenderers;
            public bool disableChildBoxColliders;
        }

        [Serializable]
        private sealed class FitPieceBoxCollider2DRequest
        {
            public string rootPath;
            public float padding;
            public bool includeDisabledRenderers;
            public bool disableRootBoxCollider;
            public bool removeRootBoxCollider;
            public bool useSpriteBounds;
        }

        [Serializable]
        private sealed class MovementSetupRequest
        {
            public string wolfPath;
            public string groundPath;
            public float moveSpeed;
            public float jumpSpeed;
            public float gravityScale;
        }

        [Serializable]
        private sealed class AnimationClipRequest
        {
            public string assetPath;
        }

        [Serializable]
        private sealed class IdleClipRequest
        {
            public string rootPath;
            public string walkClipPath;
            public string idleClipPath;
            public string controllerPath;
            public string idleStateName;
            public string walkStateName;
            public float duration;
        }

        [Serializable]
        private struct BridgeVector3
        {
            public float x;
            public float y;
            public float z;

            public Vector3 ToVector3()
            {
                return new Vector3(x, y, z);
            }
        }

        private sealed class BridgeJob
        {
            public readonly string Method;
            public readonly string Target;
            public readonly string Body;
            public readonly ManualResetEvent WaitHandle = new ManualResetEvent(false);
            public string ResponseJson;
            public int StatusCode;

            public BridgeJob(string method, string target, string body)
            {
                Method = method;
                Target = target;
                Body = body;
            }
        }
    }
}
