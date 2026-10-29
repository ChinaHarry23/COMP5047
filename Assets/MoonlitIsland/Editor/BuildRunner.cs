using UnityEditor;
using UnityEngine;

namespace MoonlitIsland.Editor
{
    /// <summary>Thin entry point so the multi-second Build() can run as a detached CLI job.</summary>
    public static class BuildRunner
    {
        public static string Run()
        {
            MoonlitIslandBuilder.Build();
            return "BuildRunner complete";
        }
    }
}
