using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Installs the packages the project depends on and removes unused template packages.
    // Versions are left to the Package Manager so they match the editor version.
    public static class LockdownProjectSetup
    {
        static readonly string[] PackagesToAdd =
        {
            "com.unity.probuilder",
            "com.unity.terrain-tools",
            "com.unity.splines",
            "com.unity.cinemachine",
            "com.unity.timeline",
            "com.unity.cloud.gltfast",
        };

        static readonly string[] PackagesToRemove =
        {
            "com.unity.collab-proxy",
            "com.unity.multiplayer.center",
            "com.unity.visualscripting",
        };

        // Installed on its own so a failure doesn't block the main batch.
        const string Polybrush = "com.unity.polybrush";

        static Request s_Request;
        static string s_Label;

        [MenuItem("Lockdown/Setup/Install Packages")]
        public static void InstallPackages()
        {
            Start(Client.AddAndRemove(PackagesToAdd, PackagesToRemove), "Install Packages");
        }

        [MenuItem("Lockdown/Setup/Install Polybrush")]
        public static void InstallPolybrush()
        {
            Start(Client.Add(Polybrush), "Install Polybrush");
        }

        static void Start(Request request, string label)
        {
            if (s_Request != null && !s_Request.IsCompleted)
            {
                Debug.LogWarning("[Lockdown] A package request is still running.");
                return;
            }
            s_Request = request;
            s_Label = label;
            Debug.Log($"[Lockdown] {label}: started.");
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (s_Request == null || !s_Request.IsCompleted) return;
            EditorApplication.update -= Poll;

            if (s_Request.Status == StatusCode.Success)
                Debug.Log($"[Lockdown] {s_Label}: done.");
            else
                Debug.LogError($"[Lockdown] {s_Label}: failed. {s_Request.Error?.message}");
            s_Request = null;
        }
    }
}
