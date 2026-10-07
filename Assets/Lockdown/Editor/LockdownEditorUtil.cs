using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Small helpers shared by the builders (imported with `using static`, so call sites read EnsureFolder(...), Group(...)).
    public static class LockdownEditorUtil
    {
        // Creates an asset folder and any missing parents.
        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // An empty child GameObject, for grouping.
        public static GameObject Group(GameObject parent, string name)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent.transform, false);
            return g;
        }
    }
}
