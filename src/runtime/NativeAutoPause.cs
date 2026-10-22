using GlobalEnums;
using UnityEngine;

// Application suspension requests the original pause flow, never an Esc pulse
// or an unpause. Keep a rejected request only in the same gameplay scene so a
// transient original pause guard can finish before the request is retried.
[DefaultExecutionOrder(-10000)]
public sealed class NativeAutoPause : MonoBehaviour
{
    GameManager pendingGame;
    string pendingScene;

    public static bool EnsurePaused(GameManager game)
    {
        if (!game) return false;
        if (game.isPaused || game.GameState == GameState.PAUSED) return true;
        if (game.GameState != GameState.PLAYING) return false;
        // StartCoroutine advances the original iterator immediately. Its pause
        // state is committed before its first yield, making repeated requests
        // harmless without modifying the game's own transition or guards.
        game.StartCoroutine(game.PauseGameToggle(playSound: false));
        return game.isPaused || game.GameState == GameState.PAUSED;
    }

    internal void OnApplicationPause(bool paused)
    {
        if (paused) RequestPause("background");
        else ApplyPending();
    }

    internal void OnApplicationFocus(bool focused)
    {
        if (!focused) RequestPause("focus-lost");
        else ApplyPending();
    }

    internal void Update() { ApplyPending(); }

    void RequestPause(string source)
    {
        var game = GameManager.UnsafeInstance;
        if (!game) return;
        if (game.isPaused || game.GameState == GameState.PAUSED)
        {
            pendingGame = null;
            if (NativePreferences.CompatibilityLogs)
                Debug.Log("NATIVE_PAUSE: kept existing pause; source=" + source + "; scene=" + game.sceneName);
            return;
        }
        if (game.GameState != GameState.PLAYING) return;
        if (pendingGame != game || pendingScene != game.sceneName)
        {
            pendingGame = game;
            pendingScene = game.sceneName;
            if (NativePreferences.CompatibilityLogs)
                Debug.Log("NATIVE_PAUSE: requested; source=" + source + "; scene=" + pendingScene);
        }
        ApplyPending();
    }

    void ApplyPending()
    {
        var game = pendingGame;
        if (!game || game != GameManager.UnsafeInstance || game.sceneName != pendingScene ||
            game.isPaused || game.GameState != GameState.PLAYING)
        {
            pendingGame = null;
            return;
        }
        if (!EnsurePaused(game)) return;
        pendingGame = null;
        if (NativePreferences.CompatibilityLogs)
            Debug.Log("NATIVE_PAUSE: paused; scene=" + game.sceneName);
    }
}
