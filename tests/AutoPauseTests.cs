using System;
using System.Collections;
using GlobalEnums;

namespace UnityEngine
{
    public class DefaultExecutionOrderAttribute : Attribute
    { public DefaultExecutionOrderAttribute(int order) {} }
    public class Object
    {
        public bool destroyed;
        public static implicit operator bool(Object value) { return value != null && !value.destroyed; }
    }
    public class MonoBehaviour : Object
    {
        public void StartCoroutine(IEnumerator routine) { routine.MoveNext(); }
    }
    public static class Debug { public static void Log(string message) {} }
}
namespace GlobalEnums
{
    public enum GameState { MAIN_MENU, PLAYING, PAUSED, CUTSCENE, EXITING_LEVEL }
}
public static class NativePreferences { public static bool CompatibilityLogs; }

// Deliberately models an unsafe toggle: any second invocation while paused
// unpauses. Production must guard it instead of relying on this fake to dedupe.
public class GameManager : UnityEngine.MonoBehaviour
{
    public static GameManager UnsafeInstance;
    public bool isPaused, originalGuardBlocks;
    public GameState GameState = GameState.PLAYING;
    public string sceneName = "Halfway_01", menu = "Gameplay";
    public int calls, pauses, unpauses;
    public IEnumerator PauseGameToggle(bool playSound)
    {
        calls++;
        if (originalGuardBlocks) yield break;
        if (GameState == GameState.PLAYING)
        {
            isPaused = true;
            GameState = GameState.PAUSED;
            menu = "PauseMenu";
            pauses++;
        }
        else if (GameState == GameState.PAUSED)
        {
            isPaused = false;
            GameState = GameState.PLAYING;
            menu = "Gameplay";
            unpauses++;
        }
        yield return null;
    }
}

static class AutoPauseTests
{
    static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    static GameManager Game()
    {
        return GameManager.UnsafeInstance = new GameManager();
    }
    static void Main()
    {
        var game = Game();
        Check(NativeAutoPause.EnsurePaused(game), "Pause a playing game");
        Check(NativeAutoPause.EnsurePaused(game), "An already paused game satisfies the request");
        Check(game.calls == 1 && game.pauses == 1 && game.unpauses == 0, "Repeated ensure never toggles back");
        game.menu = "AudioSettings";
        NativeAutoPause.EnsurePaused(game);
        Check(game.menu == "AudioSettings" && game.calls == 1, "Preserve an open pause submenu");

        var boundary = new NativeAutoPause();
        game = Game();
        boundary.OnApplicationPause(false);
        boundary.OnApplicationFocus(true);
        boundary.Update();
        Check(game.calls == 0, "Startup/resume alone cannot request pause");
        boundary.OnApplicationFocus(false);
        boundary.OnApplicationPause(true);
        boundary.OnApplicationFocus(false);
        boundary.OnApplicationFocus(true);
        boundary.OnApplicationPause(false);
        boundary.Update();
        Check(game.calls == 1 && game.isPaused && game.unpauses == 0, "Duplicate lifecycle events and resume preserve pause");
        game.menu = "VideoSettings";
        boundary.OnApplicationPause(true);
        boundary.OnApplicationPause(false);
        Check(game.menu == "VideoSettings" && game.calls == 1, "Switch away from settings preserves its page");
        game.StartCoroutine(game.PauseGameToggle(false)); // The user explicitly continues.
        boundary.OnApplicationFocus(true);
        boundary.OnApplicationPause(false);
        boundary.Update();
        Check(!game.isPaused && game.calls == 2, "Old notifications do not reapply a completed request after manual continue");

        game = Game();
        game.originalGuardBlocks = true;
        boundary.OnApplicationPause(true);
        boundary.OnApplicationFocus(false);
        boundary.OnApplicationPause(false);
        Check(game.pauses == 0 && game.unpauses == 0, "Respect the original pause guard");
        game.originalGuardBlocks = false;
        boundary.Update();
        boundary.Update();
        Check(game.isPaused && game.pauses == 1 && game.unpauses == 0, "A temporarily rejected request succeeds once after the guard clears");

        game = Game();
        game.originalGuardBlocks = true;
        boundary.OnApplicationPause(true);
        game.sceneName = "Greymoor_03";
        game.originalGuardBlocks = false;
        boundary.Update();
        Check(!game.isPaused && game.pauses == 0, "Do not leak a request into another scene");

        game = Game();
        game.originalGuardBlocks = true;
        boundary.OnApplicationPause(true);
        game = Game();
        boundary.Update();
        Check(game.calls == 0, "Do not leak a request into a replacement game manager");

        game.originalGuardBlocks = true;
        boundary.OnApplicationPause(true);
        game.originalGuardBlocks = false;
        game.StartCoroutine(game.PauseGameToggle(false));
        game.menu = "ExistingMenu";
        int before = game.calls;
        boundary.Update();
        Check(game.calls == before && game.menu == "ExistingMenu", "Another pause satisfies the pending request without toggling or resetting its menu");

        foreach (GameState state in new[] { GameState.MAIN_MENU, GameState.CUTSCENE, GameState.EXITING_LEVEL })
        {
            game = Game();
            game.GameState = state;
            boundary.OnApplicationFocus(false);
            boundary.OnApplicationPause(true);
            Check(!NativeAutoPause.EnsurePaused(game) && game.calls == 0, "Ignore non-gameplay: " + state);
            game.GameState = GameState.PLAYING;
            boundary.OnApplicationFocus(true);
            boundary.Update();
            Check(game.calls == 0, "Non-gameplay loss does not schedule a future pause");
        }
        GameManager.UnsafeInstance = null;
        boundary.OnApplicationPause(true);
        boundary.Update();
        Check(!NativeAutoPause.EnsurePaused(null), "No game manager is harmless");
        Console.WriteLine("PASS idempotent pause, duplicate events, resume, submenus, original guards, scene and manager changes");
    }
}
