using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Generic curve-binding compatibility for desktop bundles on the native player.
public sealed class NativeAnimatorCompatibility : MonoBehaviour
{
    sealed class Entry
    {
        public Animator animator;
        public RuntimeAnimatorController controller;
        public bool pending = true, unsupported;
        public bool queued;
        public int initializationRetries;
        public float nextAttempt;
    }
    struct Parameter
    {
        public int hash, integer;
        public float number;
        public bool boolean;
        public AnimatorControllerParameterType type;
    }
    static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
    static readonly List<int> removed = new List<int>();
    static readonly List<Entry> pending = new List<Entry>();
    static bool installed, repairing;
    static float nextDiscovery;
    static int repaired, deferred, unsupported, failures, detailed, attempts, discoveries;
    static long repairTicks, discoveryTicks;
    float nextReport;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        SceneManager.sceneLoaded += OnSceneLoaded;
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_ANIMATOR: general binding repair v2; activation-driven queue; dormant retries at discovery only; preserves state/time/parameters/weights and frozen poses; enable boundary active");
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) { nextDiscovery = 0f; }

    // Called after the native Behaviour.enabled setter, only on false -> true.
    // The original caller then continues with its own Play/state selection.
    public static void AfterEnabled(Behaviour behaviour)
    {
        if (!installed || repairing) return;
        Animator animator = behaviour as Animator;
        if (animator) Request(animator);
    }

    public static void Activated(Animator animator)
    {
        if (installed && !repairing && animator) Request(animator);
    }

    static Entry Register(Animator animator)
    {
        int id = animator.GetInstanceID();
        Entry entry;
        if (!entries.TryGetValue(id, out entry) || entry.animator != animator)
        {
            entry = new Entry { animator = animator };
            entries[id] = entry;
        }
        if (entry.controller != animator.runtimeAnimatorController)
        {
            entry.controller = animator.runtimeAnimatorController;
            entry.pending = true;
            entry.unsupported = false;
            entry.initializationRetries = 0;
        }
        return entry;
    }

    static void Request(Animator animator)
    {
        if (!animator.gameObject.scene.IsValid() || !animator.runtimeAnimatorController) return;
        Entry entry = Register(animator);
        entry.pending = true;
        entry.initializationRetries = 0;
        if (Attempt(entry, Time.unscaledTime)) Queue(entry);
    }

    static void Queue(Entry entry)
    {
        if (entry.queued || !entry.pending || entry.unsupported) return;
        entry.queued = true;
        pending.Add(entry);
    }

    // True means a short, bounded retry is useful. Dormant objects remain
    // registered but consume no per-frame work; activation wakes them directly.
    static bool Attempt(Entry entry, float now)
    {
        if (!entry.animator || !entry.pending || entry.unsupported) return false;
        bool diagnostics = NativePreferences.CompatibilityLogs;
        if (diagnostics) attempts++;
        long start = diagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        string reason;
        bool frozen = !entry.animator.enabled;
        bool success = RepairPreservingState(entry.animator, out reason);
        if (diagnostics) repairTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        if (success)
        {
            entry.pending = false;
            if (diagnostics) repaired++;
            if (diagnostics && detailed++ < 40)
                Debug.Log("NATIVE_ANIMATOR: repaired " + entry.animator.gameObject.scene.name + "/" + entry.animator.name + " frozen=" + frozen);
        }
        else if (reason == "state-machine-behaviour" || reason == "root-motion")
        {
            // Rebind can re-enter custom state callbacks or root-motion hooks.
            // Do not replay gameplay callbacks to repair a visual binding.
            entry.unsupported = true;
            if (diagnostics) unsupported++;
            if (diagnostics && detailed++ < 40) Debug.Log("NATIVE_ANIMATOR: left original " + entry.animator.name + " reason=" + reason);
        }
        else if (reason.StartsWith("error:"))
        {
            entry.unsupported = true;
            failures++;
            Debug.LogWarning("NATIVE_ANIMATOR: " + entry.animator.name + " " + reason);
        }
        else
        {
            if (diagnostics) deferred++;
            if (reason == "transition")
            {
                // Never interrupt a blend. A 20 Hz retry is sufficient once
                // the original transition ends; playback itself is untouched.
                entry.nextAttempt = now + .05f;
                return true;
            }
            if (entry.animator.gameObject.activeInHierarchy && entry.animator.enabled &&
                entry.initializationRetries++ < 2)
            {
                // Give newly activated components two player-loop passes to
                // initialize. Longer waits fall back to the discovery sweep.
                entry.nextAttempt = now;
                return true;
            }
        }
        return false;
    }

    // Public for the isolated real-player-loop regression fixture.
    // No enable toggling: disabled components on active objects can be sampled
    // by Update(0), retaining the frozen pose and their disabled state.
    public static bool RepairPreservingState(Animator animator, out string reason)
    {
        reason = "not-ready";
        if (repairing || !animator || !animator.runtimeAnimatorController ||
            !animator.gameObject.activeInHierarchy || !animator.isInitialized || animator.layerCount == 0) return false;
        if (animator.applyRootMotion) { reason = "root-motion"; return false; }
        int layers = animator.layerCount;
        for (int i = 0; i < layers; i++)
            if (animator.IsInTransition(i)) { reason = "transition"; return false; }
        if (animator.GetBehaviours<StateMachineBehaviour>().Length != 0)
        { reason = "state-machine-behaviour"; return false; }
        var states = new AnimatorStateInfo[layers];
        var weights = new float[layers];
        for (int i = 0; i < layers; i++)
        {
            states[i] = animator.GetCurrentAnimatorStateInfo(i);
            weights[i] = animator.GetLayerWeight(i);
            if (states[i].fullPathHash == 0) return false;
        }
        var definitions = animator.parameters;
        var parameters = new Parameter[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
        {
            var p = new Parameter { hash = definitions[i].nameHash, type = definitions[i].type };
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: p.number = animator.GetFloat(p.hash); break;
                case AnimatorControllerParameterType.Int: p.integer = animator.GetInteger(p.hash); break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger: p.boolean = animator.GetBool(p.hash); break;
            }
            parameters[i] = p;
        }
        bool events = animator.fireEvents;
        float speed = animator.speed;
        AnimatorCullingMode culling = animator.cullingMode;
        repairing = true;
        try
        {
            animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // Restore the exact current state on every layer, including
            // end/loop times, before the player can render another frame.
            animator.Rebind();
            // Blend trees must see their real parameters when reconstructing
            // a frozen pose. Do not let default float/bool values select it.
            RestoreParameters(animator, parameters, false);
            for (int i = 0; i < layers; i++)
            {
                animator.SetLayerWeight(i, weights[i]);
                animator.Play(states[i].fullPathHash, i, states[i].normalizedTime);
            }
            // Sample without advancing time or emitting AnimationEvents.
            // Pending triggers are restored afterwards, not consumed here.
            animator.Update(0f);
            reason = "repaired";
            return true;
        }
        catch (Exception ex) { reason = "error:" + ex.GetType().Name + ":" + ex.Message; return false; }
        finally
        {
            if (animator)
            {
                RestoreParameters(animator, parameters, true);
                animator.speed = speed;
                animator.cullingMode = culling;
                animator.fireEvents = events;
            }
            repairing = false;
        }
    }

    static void RestoreParameters(Animator animator, Parameter[] parameters, bool triggers)
    {
        foreach (var p in parameters)
        {
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: animator.SetFloat(p.hash, p.number); break;
                case AnimatorControllerParameterType.Int: animator.SetInteger(p.hash, p.integer); break;
                case AnimatorControllerParameterType.Bool: animator.SetBool(p.hash, p.boolean); break;
                case AnimatorControllerParameterType.Trigger:
                    if (triggers && p.boolean) animator.SetTrigger(p.hash); else animator.ResetTrigger(p.hash);
                    break;
            }
        }
    }

    void Update()
    {
        float now = Time.unscaledTime;
        if (now < nextDiscovery) return;
        nextDiscovery = now + 2f;
        Discover(now);
    }

    static void Discover(float now)
    {
        bool diagnostics = NativePreferences.CompatibilityLogs;
        long start = diagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (diagnostics) discoveries++;
        foreach (var animator in Resources.FindObjectsOfTypeAll<Animator>())
        {
            if (!animator.gameObject.scene.IsValid() || !animator.runtimeAnimatorController) continue;
            Entry entry = Register(animator);
            var guard = animator.GetComponent<NativeAnimatorActivationGuard>();
            if (!guard) guard = animator.gameObject.AddComponent<NativeAnimatorActivationGuard>();
            guard.animator = animator;
            if (entry.pending && !entry.unsupported && !entry.queued && animator.gameObject.activeInHierarchy)
            {
                entry.initializationRetries = 0;
                entry.nextAttempt = now;
                Queue(entry);
            }
        }
        removed.Clear();
        foreach (var pair in entries) if (!pair.Value.animator) removed.Add(pair.Key);
        foreach (int id in removed) entries.Remove(id);
        if (diagnostics) discoveryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
    }

    static void Drain(float now)
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            Entry entry = pending[i];
            if (entry.animator && entry.pending && !entry.unsupported &&
                (now < entry.nextAttempt || Attempt(entry, now))) continue;
            entry.queued = false;
            pending.RemoveAt(i);
        }
    }

    void LateUpdate()
    {
        Drain(Time.unscaledTime);
        if (!NativePreferences.CompatibilityLogs || Time.unscaledTime < nextReport) return;
        nextReport = Time.unscaledTime + 30f;
        double milliseconds = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Debug.Log("NATIVE_ANIMATOR: tracked=" + entries.Count + " queued=" + pending.Count + " repaired=" + repaired + " attempts=" + attempts + " deferred=" + deferred + " skippedCallbacksOrRootMotion=" + unsupported + " errors=" + failures + " discoveryPasses=" + discoveries + " discoveryMs=" + (discoveryTicks * milliseconds).ToString("F2") + " repairMs=" + (repairTicks * milliseconds).ToString("F2"));
    }
}

// No per-object Update: only GameObject reactivation needs this notification.
public sealed class NativeAnimatorActivationGuard : MonoBehaviour
{
    [NonSerialized] public Animator animator;
    void OnEnable() { NativeAnimatorCompatibility.Activated(animator); }
}
