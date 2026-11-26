using System;
using System.Collections.Generic;

namespace Pauto
{
    public enum SessionKind
    {
        Other,
        Browser,
        Spotifast
    }

    public enum PlaybackAction
    {
        None,
        PauseSpotifast,
        ScheduleResume,
        CancelPendingResume,
        ResumeSpotifast
    }

    public static class SessionClassifier
    {
        private static readonly char[] TokenSeparators = new char[] { '\\', '/', '.', '_', '-', '!', ' ', ':' };

        private static readonly HashSet<string> BrowserTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome",
            "chromium",
            "msedge",
            "microsoftedge",
            "brave",
            "opera",
            "operagx",
            "operastable",
            "firefox",
            "vivaldi",
            "zen",
            "arc",
            "waterfox",
            "floorp",
            "thorium",
            "librewolf",
            "308046b0af4a39cb"
        };

        public static SessionKind Classify(string appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return SessionKind.Other;
            }

            if (appUserModelId.IndexOf("spotifast", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SessionKind.Spotifast;
            }

            string[] tokens = appUserModelId.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (BrowserTokens.Contains(tokens[i]))
                {
                    return SessionKind.Browser;
                }
            }

            return SessionKind.Other;
        }
    }

    public sealed class AutoPauseController
    {
        public const int ResumeDebounceMs = 350;

        private bool _wasBrowserPlaying;
        private bool _awaitingPauseConfirmation;

        public bool Enabled { get; private set; }
        public bool PausedByUs { get; private set; }
        public bool ResumePending { get; private set; }

        public AutoPauseController()
        {
            Enabled = true;
        }

        public PlaybackAction Update(bool anyBrowserPlaying, bool spotifastPlaying, bool spotifastExists)
        {
            bool prevBrowserPlaying = _wasBrowserPlaying;
            _wasBrowserPlaying = anyBrowserPlaying;

            if (!Enabled)
            {
                return PlaybackAction.None;
            }

            if (_awaitingPauseConfirmation && !spotifastPlaying)
            {
                _awaitingPauseConfirmation = false;
            }

            if (!prevBrowserPlaying && anyBrowserPlaying)
            {
                bool wasPending = ResumePending;
                ResumePending = false;

                if (spotifastExists && spotifastPlaying)
                {
                    return PlaybackAction.PauseSpotifast;
                }

                if (wasPending)
                {
                    return PlaybackAction.CancelPendingResume;
                }

                return PlaybackAction.None;
            }

            if (prevBrowserPlaying && anyBrowserPlaying)
            {
                if (PausedByUs && !_awaitingPauseConfirmation && spotifastPlaying)
                {
                    PausedByUs = false;
                }

                return PlaybackAction.None;
            }

            if (prevBrowserPlaying && !anyBrowserPlaying)
            {
                if (!PausedByUs)
                {
                    return PlaybackAction.None;
                }

                if (!spotifastExists || (!_awaitingPauseConfirmation && spotifastPlaying))
                {
                    PausedByUs = false;
                    _awaitingPauseConfirmation = false;
                    return PlaybackAction.None;
                }

                ResumePending = true;
                return PlaybackAction.ScheduleResume;
            }

            if (ResumePending && (!spotifastExists || (!_awaitingPauseConfirmation && spotifastPlaying)))
            {
                ResumePending = false;
                PausedByUs = false;
                _awaitingPauseConfirmation = false;
                return PlaybackAction.CancelPendingResume;
            }

            return PlaybackAction.None;
        }

        public PlaybackAction OnResumeTimerElapsed(bool anyBrowserPlaying, bool spotifastPlaying, bool spotifastExists)
        {
            _wasBrowserPlaying = anyBrowserPlaying;
            ResumePending = false;
            _awaitingPauseConfirmation = false;

            if (!Enabled || !PausedByUs)
            {
                PausedByUs = false;
                return PlaybackAction.None;
            }

            PausedByUs = false;

            if (!anyBrowserPlaying && spotifastExists && !spotifastPlaying)
            {
                return PlaybackAction.ResumeSpotifast;
            }

            return PlaybackAction.None;
        }

        public void NotifyPauseSucceeded(bool spotifastStillPlaying = true)
        {
            PausedByUs = true;
            _awaitingPauseConfirmation = spotifastStillPlaying;
        }

        public void NotifyPauseFailed()
        {
            PausedByUs = false;
            _awaitingPauseConfirmation = false;
        }

        public void NotifyResumeCompleted()
        {
            PausedByUs = false;
            ResumePending = false;
            _awaitingPauseConfirmation = false;
        }

        public PlaybackAction SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if (!enabled)
            {
                bool wasPending = ResumePending;
                PausedByUs = false;
                ResumePending = false;
                _awaitingPauseConfirmation = false;
                return wasPending ? PlaybackAction.CancelPendingResume : PlaybackAction.None;
            }

            return PlaybackAction.None;
        }
    }
}
