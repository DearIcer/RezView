using System;
using UnityEditor;
using UnityEditor.Build.Reporting;

// Command-line entry point for rebuilding the CFRezManager model viewer:
//   Unity.exe -batchmode -quit -projectPath <this project> -executeMethod ViewerBuildScript.BuildViewer
// The output path matches CFRezManager's UnityViewerLocator default (tools\UnityModelViewer\).
internal static class ViewerBuildScript
{
    private const string OutputPath = @"D:\Github-WebProject\CFRezManager\tools\UnityModelViewer\CFRezModelViewer.exe";

    public static void BuildViewer()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = OutputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception("Viewer build failed: " + report.summary.result);
        }
    }
}
