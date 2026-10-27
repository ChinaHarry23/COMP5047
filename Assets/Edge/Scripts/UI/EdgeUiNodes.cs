using System.Collections.Generic;
using UnityEngine;

namespace Edge.UI
{
    public enum UiKind : byte { Image, Text }
    public enum UiFont : byte { Title, Body, BodyBold }

    // One element of a screen, produced by a pure layout function and applied to uGUI by EdgeUiView
    // (or drawn by the offline preview). Rects are in canvas units, origin top-left, y down.
    public struct UiNode
    {
        public string id;          // stable key: the same id reuses the same GameObject
        public UiKind kind;
        public Rect rect;
        public Color color;
        // Image
        public string sprite;      // EdgeUiArt id
        public float slice;        // > 0: 9-sliced, value = pixelsPerUnitMultiplier (sprite border px / slice = canvas units)
        public float fill;         // < 1: radial fill from the top, clockwise
        // Text
        public string text;
        public UiFont font;
        public int size;
        public TextAnchor align;
        public bool shadow;
    }

    // Small helpers so layouts read like a description of the screen.
    public sealed class UiList
    {
        public readonly List<UiNode> nodes = new List<UiNode>(160);

        public void Clear() => nodes.Clear();

        public void Image(string id, Rect r, string sprite, Color c, float slice = 0f, float fill = 1f)
        {
            if (c.a <= 0.003f || r.width <= 0f || r.height <= 0f) return;
            nodes.Add(new UiNode { id = id, kind = UiKind.Image, rect = r, sprite = sprite, color = c, slice = slice, fill = fill });
        }

        public void Text(string id, Rect r, string text, UiFont font, int size, Color c, TextAnchor align, bool shadow = true)
        {
            if (c.a <= 0.003f || string.IsNullOrEmpty(text)) return;
            nodes.Add(new UiNode { id = id, kind = UiKind.Text, rect = r, text = text, font = font, size = size, color = c, align = align, shadow = shadow });
        }
    }
}
