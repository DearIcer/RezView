using System;
using System.IO;
using UnityEngine;

// Entry point for the CFRezManager embedded model viewer. Runs automatically after the
// scene loads so the stock URP template scene needs no manual changes: existing cameras
// and lights from the template are destroyed and replaced with our own.
internal sealed class ViewerBootstrap : MonoBehaviour
{
    private static readonly Color BackgroundColor = new Color32(0x11, 0x18, 0x27, 0xFF);

    private string _errorMessage;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Start()
    {
        var host = new GameObject("CFRezViewer");
        host.AddComponent<ViewerBootstrap>();
    }

    private void Awake()
    {
        foreach (Camera camera in FindObjectsOfType<Camera>())
        {
            Destroy(camera.gameObject);
        }

        foreach (Light light in FindObjectsOfType<Light>())
        {
            Destroy(light.gameObject);
        }

        string modelPath = GetArgFromArgs("--cfrez-model");
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            _errorMessage = "Missing --cfrez-model <path> argument.";
            Debug.LogError(_errorMessage);
            return;
        }

        string animPackagePath = GetArgFromArgs("--cfrez-anim");

        GameObject model;
        Bounds bounds;
        try
        {
            RuntimeAnimPackageLoader.LoadResult animModel = null;
            if (!string.IsNullOrWhiteSpace(animPackagePath) && File.Exists(animPackagePath))
            {
                animModel = RuntimeAnimPackageLoader.Load(animPackagePath);
            }

            if (animModel != null)
            {
                model = animModel.Root;
                bounds = animModel.Bounds;
                if (animModel.Animation != null && animModel.AnimationCount > 0)
                {
                    // Default to the first track; the watcher overrides this once it reads
                    // the command file written by the preview window.
                    animModel.Animation.Play("anim_0");
                    AnimCommandWatcher.Attach(
                        model,
                        Path.Combine(Path.GetDirectoryName(animPackagePath) ?? string.Empty, "anim-command.txt"),
                        animModel.Animation,
                        animModel.Bones,
                        animModel.AnimationCount,
                        initialTrack: 0);
                }
            }
            else
            {
                model = RuntimeObjLoader.Load(modelPath, out bounds);
            }
        }
        catch (Exception ex)
        {
            _errorMessage = "Failed to load model: " + ex.Message;
            Debug.LogError(_errorMessage);
            return;
        }

        SetupLighting();

        var cameraObject = new GameObject("ViewerCamera");
        Camera viewerCamera = cameraObject.AddComponent<Camera>();
        viewerCamera.clearFlags = CameraClearFlags.SolidColor;
        viewerCamera.backgroundColor = BackgroundColor;
        viewerCamera.fieldOfView = 45f;
        viewerCamera.nearClipPlane = 0.01f;
        viewerCamera.farClipPlane = 1000f;
        cameraObject.tag = "MainCamera";

        // Match the WPF preview's initial orbit (yaw -35 / pitch 22 / distance 8),
        // scaled to the model bounds and mapped through the same Z flip as the loader.
        float distance = Mathf.Max(bounds.extents.magnitude * 1.8f, 1f);
        const float orbitYaw = -35f * Mathf.Deg2Rad;
        const float orbitPitch = 22f * Mathf.Deg2Rad;
        float horizontal = distance * Mathf.Cos(orbitPitch);
        Vector3 offset = new Vector3(
            horizontal * Mathf.Sin(orbitYaw),
            distance * Mathf.Sin(orbitPitch),
            -horizontal * Mathf.Cos(orbitYaw));
        cameraObject.transform.position = bounds.center + offset;
        cameraObject.transform.LookAt(bounds.center, Vector3.up);

        cameraObject.AddComponent<FlyCamera>().SetHome(cameraObject.transform.position, cameraObject.transform.rotation);
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_errorMessage))
        {
            return;
        }

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 16,
            wordWrap = true
        };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(20, 20, Screen.width - 40, Screen.height - 40), _errorMessage, style);
    }

    private static void SetupLighting()
    {
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f);

        CreateDirectionalLight("KeyLight", new Vector3(50f, -30f, 0f), 0.9f);
        CreateDirectionalLight("FillLight", new Vector3(-35f, 20f, 0f), 0.45f);
    }

    private static void CreateDirectionalLight(string name, Vector3 eulerAngles, float intensity)
    {
        var lightObject = new GameObject(name);
        lightObject.transform.rotation = Quaternion.Euler(eulerAngles);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
    }

    private static string GetArgFromArgs(string argName)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], argName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
