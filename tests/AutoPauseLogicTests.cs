using System;
using System.Collections.Generic;
using Pauto;

namespace Pauto.Tests
{
    internal static class AutoPauseLogicTests
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Run("Classifier recognizes Spotifast IDs", TestClassifierSpotifast);
            Run("Classifier recognizes desktop browser IDs", TestClassifierBrowsers);
            Run("Classifier ignores non-browser apps and empty values", TestClassifierOther);
            Run("Browser play pauses Spotifast and stop resumes after debounce", TestBasicPauseAndResume);
            Run("Manually paused Spotifast is never auto-resumed", TestManualPauseRespected);
            Run("Transient pre-pause SMTC Playing event does not drop ownership", TestTransientPrePauseEvent);
            Run("Manual resume works even when Spotifast already reported Paused before pause callback returns", TestAlreadyPausedBeforeNotifyCallback);
            Run("Manual resume during browser playback releases ownership without fighting", TestManualResumeDuringBrowserPlay);
            Run("Multiple browser sessions stay paused until all stop", TestMultipleBrowserSessions);
            Run("Brief seek flicker cancels pending resume and keeps ownership", TestAntiSeekFlickerCancelsResume);
            Run("Manual resume during debounce window cancels pending resume", TestManualResumeDuringDebounceWindow);
            Run("Spotifast closing before debounce elapses cancels resume cleanly", TestSpotifastClosedBeforeResume);
            Run("Failed pause command does not claim ownership", TestPauseFailedDoesNotClaimOwnership);
            Run("Disabling clears state and re-enabling starts cleanly", TestDisableAndReenable);

            Console.WriteLine(string.Format("Result: {0} passed, {1} failed.", _passed, _failed));
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("  PASS: " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FAIL: " + name + " -> " + ex.Message);
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string context)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(string.Format("{0}: expected <{1}>, got <{2}>", context, expected, actual));
            }
        }

        private static void TestClassifierSpotifast()
        {
            AssertEqual(SessionKind.Spotifast, SessionClassifier.Classify("spotifast.exe"), "spotifast.exe");
            AssertEqual(SessionKind.Spotifast, SessionClassifier.Classify("Spotifast"), "Spotifast");
            AssertEqual(SessionKind.Spotifast, SessionClassifier.Classify("com.spotifast.client"), "com.spotifast.client");
        }

        private static void TestClassifierBrowsers()
        {
            string[] ids = new string[]
            {
                "chrome.exe",
                "Chrome",
                "MSEdge",
                "msedge.exe",
                "brave.exe",
                "opera.exe",
                "firefox.exe",
                "308046B0AF4A39CB",
                "vivaldi.exe",
                "zen.exe",
                "arc.exe",
                "TheBrowserCompany.Arc_ttt1ap7aakyb4!Arc",
                "chromium.exe",
                "waterfox.exe",
                "floorp.exe",
                "thorium.exe",
                "librewolf.exe"
            };

            for (int i = 0; i < ids.Length; i++)
            {
                AssertEqual(SessionKind.Browser, SessionClassifier.Classify(ids[i]), ids[i]);
            }
        }

        private static void TestClassifierOther()
        {
            AssertEqual(SessionKind.Other, SessionClassifier.Classify("vlc.exe"), "vlc.exe");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify("Discord.exe"), "Discord.exe");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify("Telegram.exe"), "Telegram.exe");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify("Spotify.exe"), "Spotify.exe");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify("SearchHost.exe"), "SearchHost.exe (must not match arc)");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify("Citizen.exe"), "Citizen.exe (must not match zen)");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify("Knowledge.exe"), "Knowledge.exe (must not match edge)");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify(""), "empty");
            AssertEqual(SessionKind.Other, SessionClassifier.Classify(null), "null");
        }

        private static void TestBasicPauseAndResume()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.None, ctrl.Update(false, true, true), "initial Spotifast playing");
            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "browser starts playing");
            ctrl.NotifyPauseSucceeded();
            AssertEqual(true, ctrl.PausedByUs, "PausedByUs after success");

            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast confirms paused");
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "browser stops");
            AssertEqual(true, ctrl.ResumePending, "ResumePending armed");

            AssertEqual(PlaybackAction.ResumeSpotifast, ctrl.OnResumeTimerElapsed(false, false, true), "debounce elapsed");
            ctrl.NotifyResumeCompleted();
            AssertEqual(false, ctrl.PausedByUs, "PausedByUs cleared");
            AssertEqual(false, ctrl.ResumePending, "ResumePending cleared");
        }

        private static void TestManualPauseRespected()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.None, ctrl.Update(false, false, true), "Spotifast manually paused");
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "browser starts playing");
            AssertEqual(false, ctrl.PausedByUs, "ownership not claimed");
            AssertEqual(PlaybackAction.None, ctrl.Update(false, false, true), "browser stops");
            AssertEqual(false, ctrl.ResumePending, "no resume scheduled");
        }

        private static void TestTransientPrePauseEvent()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "browser starts");
            ctrl.NotifyPauseSucceeded();

            // SMTC still reports Spotifast as Playing before the pause completes
            AssertEqual(PlaybackAction.None, ctrl.Update(true, true, true), "transient still-playing event");
            AssertEqual(true, ctrl.PausedByUs, "ownership preserved while awaiting pause confirmation");

            // Spotifast now reports Paused, then browser stops
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast confirms paused");
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "browser stops");
        }

        private static void TestAlreadyPausedBeforeNotifyCallback()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "browser starts");
            ctrl.NotifyPauseSucceeded(false);

            // User immediately resumes Spotifast manually while browser continues playing
            AssertEqual(PlaybackAction.None, ctrl.Update(true, true, true), "must not fight manual play");
            AssertEqual(false, ctrl.PausedByUs, "ownership released immediately");
        }

        private static void TestManualResumeDuringBrowserPlay()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "browser starts");
            ctrl.NotifyPauseSucceeded();
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast confirms paused");

            // User intentionally hits Play on Spotifast while video continues
            AssertEqual(PlaybackAction.None, ctrl.Update(true, true, true), "must not fight manual play");
            AssertEqual(false, ctrl.PausedByUs, "ownership released on manual play");

            // Browser stops later -> should NOT try to resume or interfere
            AssertEqual(PlaybackAction.None, ctrl.Update(false, true, true), "browser stops while Spotifast plays");
        }

        private static void TestMultipleBrowserSessions()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "tab 1 starts");
            ctrl.NotifyPauseSucceeded();
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast pauses");

            // Tab 1 stops but Tab 2 is still playing -> anyBrowserPlaying remains true
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "tab 2 still playing");
            AssertEqual(true, ctrl.PausedByUs, "still owned");
            AssertEqual(false, ctrl.ResumePending, "not resuming yet");

            // Tab 2 finally stops
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "all tabs stopped");
        }

        private static void TestAntiSeekFlickerCancelsResume()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "video starts");
            ctrl.NotifyPauseSucceeded();
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast confirms paused");

            // YouTube timeline seek briefly flips browser to Paused, then back to Playing within 350ms
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "brief pause schedules resume");
            AssertEqual(PlaybackAction.CancelPendingResume, ctrl.Update(true, false, true), "quick resume cancels timer");
            AssertEqual(false, ctrl.ResumePending, "timer cleared");
            AssertEqual(true, ctrl.PausedByUs, "ownership kept");

            // Video actually pauses later
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "real pause schedules resume");
            AssertEqual(PlaybackAction.ResumeSpotifast, ctrl.OnResumeTimerElapsed(false, false, true), "resumes cleanly");
        }

        private static void TestManualResumeDuringDebounceWindow()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "video starts");
            ctrl.NotifyPauseSucceeded();
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast pauses");
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "video stops");

            // User hits Play manually in Spotifast before 350ms passes
            AssertEqual(PlaybackAction.CancelPendingResume, ctrl.Update(false, true, true), "manual play cancels debounce");
            AssertEqual(false, ctrl.PausedByUs, "ownership cleared");
            AssertEqual(false, ctrl.ResumePending, "pending cleared");
        }

        private static void TestSpotifastClosedBeforeResume()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "video starts");
            ctrl.NotifyPauseSucceeded();
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast pauses");
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "video stops");

            // Spotifast is closed before timer fires
            AssertEqual(PlaybackAction.None, ctrl.OnResumeTimerElapsed(false, false, false), "closed Spotifast does not resume");
            AssertEqual(false, ctrl.PausedByUs, "ownership cleared");
        }

        private static void TestPauseFailedDoesNotClaimOwnership()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "video starts");
            ctrl.NotifyPauseFailed();
            AssertEqual(false, ctrl.PausedByUs, "ownership stays false");
            AssertEqual(PlaybackAction.None, ctrl.Update(false, false, true), "video stops without scheduling resume");
        }

        private static void TestDisableAndReenable()
        {
            var ctrl = new AutoPauseController();

            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "video starts");
            ctrl.NotifyPauseSucceeded();
            AssertEqual(PlaybackAction.None, ctrl.Update(true, false, true), "Spotifast pauses");
            AssertEqual(PlaybackAction.ScheduleResume, ctrl.Update(false, false, true), "video stops");

            AssertEqual(PlaybackAction.CancelPendingResume, ctrl.SetEnabled(false), "disable cancels pending resume");
            AssertEqual(false, ctrl.Enabled, "Enabled is false");
            AssertEqual(false, ctrl.PausedByUs, "ownership cleared");
            AssertEqual(false, ctrl.ResumePending, "pending cleared");

            // While disabled, browser play does nothing
            AssertEqual(PlaybackAction.None, ctrl.Update(true, true, true), "ignored while disabled");
            AssertEqual(PlaybackAction.None, ctrl.SetEnabled(true), "re-enabled cleanly");

            // Browser is already playing -> must not suddenly pause until a fresh transition
            AssertEqual(PlaybackAction.None, ctrl.Update(true, true, true), "already playing after re-enable");
            AssertEqual(PlaybackAction.None, ctrl.Update(false, true, true), "browser stops");
            AssertEqual(PlaybackAction.PauseSpotifast, ctrl.Update(true, true, true), "next browser play pauses again");
        }
    }
}
