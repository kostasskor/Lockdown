using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Locks the Game view to a fixed 1920x1080 resolution, so reviews match the target screen.
    // Unity has no public API for Game view sizes, so this goes through the internal GameViewSizes.
    public static class LockdownGameView
    {
        const int Width = 1920;
        const int Height = 1080;
        const string Label = "Lockdown 1080p";

        [MenuItem("Lockdown/Review/Lock Game View 1080p")]
        public static void Lock1080p()
        {
            Assembly asm = typeof(Editor).Assembly;
            Type sizesType = asm.GetType("UnityEditor.GameViewSizes");
            Type singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            object sizes = singleton.GetProperty("instance").GetValue(null);
            object group = sizesType.GetProperty("currentGroup").GetValue(sizes);
            Type groupType = group.GetType();

            int index = FindSize(group, groupType);
            if (index < 0)
            {
                Type sizeType = asm.GetType("UnityEditor.GameViewSize");
                Type sizeKind = asm.GetType("UnityEditor.GameViewSizeType");
                object fixedRes = Enum.Parse(sizeKind, "FixedResolution");
                object size = Activator.CreateInstance(sizeType, fixedRes, Width, Height, Label);
                groupType.GetMethod("AddCustomSize").Invoke(group, new[] { size });
                sizesType.GetMethod("SaveToHDD")?.Invoke(sizes, null);
                index = FindSize(group, groupType);
            }

            Type gameViewType = asm.GetType("UnityEditor.GameView");
            EditorWindow gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo selected = gameViewType.GetProperty("selectedSizeIndex", flags);
            if (selected != null && selected.CanWrite)
                selected.SetValue(gameView, index);
            else
                gameViewType.GetMethod("SizeSelectionCallback", flags)?.Invoke(gameView, new object[] { index, null });
            gameView.Repaint();
            Debug.Log($"[Lockdown] Game view locked to {Width}x{Height} (size index {index}).");
        }

        static int FindSize(object group, Type groupType)
        {
            int count = (int)groupType.GetMethod("GetTotalCount").Invoke(group, null);
            MethodInfo get = groupType.GetMethod("GetGameViewSize");
            for (int i = 0; i < count; i++)
            {
                object size = get.Invoke(group, new object[] { i });
                Type t = size.GetType();
                int w = (int)t.GetProperty("width").GetValue(size);
                int h = (int)t.GetProperty("height").GetValue(size);
                string kind = t.GetProperty("sizeType").GetValue(size).ToString();
                if (w == Width && h == Height && kind == "FixedResolution") return i;
            }
            return -1;
        }
    }
}
