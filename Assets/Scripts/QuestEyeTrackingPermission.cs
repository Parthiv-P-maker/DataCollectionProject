using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/// <summary>Asks for the Quest Pro eye-tracking permission when the app starts. No scene setup needed.</summary>
public static class QuestEyeTrackingPermission
{
    public const string PermissionId = "com.oculus.permission.EYE_TRACKING";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Request()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(PermissionId))
            Permission.RequestUserPermission(PermissionId);
#endif
    }
}
