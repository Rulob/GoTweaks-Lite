using NLog;
using RTSSSharedMemoryNET;
using Shared.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Systems;

namespace XboxGamingBarHelper.AutoTDP
{
    /// <summary>
    /// Automatic TDP controller: continuously adjusts TDP (via
    /// <see cref="PerformanceManager.SetAutoTDP"/>) to track a target FPS, using a conservative
    /// PID loop with dead-zone hysteresis, FPS-cap probing (finds the minimum TDP that still
    /// hits target), sweet-spot detection, and ceiling detection (backs off gracefully when the
    /// target FPS is unreachable, e.g. CPU-bound games). Only manages TDP while a game is running,
    /// focused (unless disabled), and the device is in Custom TDP mode.
    /// </summary>
    internal class AutoTDPManager : Manager
    {
        private readonly PerformanceManager performanceManager;
        private readonly SystemManager systemManager;

        // Target FPS change tracking - reset state when target changes
        private int lastTargetFPS = 0;
        private DateTime lastTargetChangeTime = DateTime.MinValue;
        private const double TargetChangeSettleTimeMs = 2000;  // Ignore spike detection for 2 seconds after target change

        // Current game tracking (for ceiling-detection warmup only)
        private string currentGamePath = null;
        private string currentGameName = null;
        private DateTime gameStartTime = DateTime.MinValue;  // When current game was detected

        // Properties
        private readonly AutoTDPEnabledProperty enabled;
        public AutoTDPEnabledProperty Enabled => enabled;

        private readonly AutoTDPTargetFPSProperty targetFPS;
        public AutoTDPTargetFPSProperty TargetFPS => targetFPS;

        private readonly AutoTDPCurrentFPSProperty currentFPS;
        public AutoTDPCurrentFPSProperty CurrentFPS => currentFPS;

        private readonly AutoTDPMinTDPProperty minTDPProperty;
        public AutoTDPMinTDPProperty MinTDP => minTDPProperty;

        private readonly AutoTDPMaxTDPProperty maxTDPProperty;
        public AutoTDPMaxTDPProperty MaxTDP => maxTDPProperty;

        private readonly AutoTDPPauseWhenUnfocusedProperty pauseWhenUnfocused;
        public AutoTDPPauseWhenUnfocusedProperty PauseWhenUnfocused => pauseWhenUnfocused;

        // Controller state
        private double integral = 0;
        private DateTime lastUpdateTime = DateTime.MinValue;
        private int lastAppliedTDP = 15;
        private int consecutiveStableReadings = 0;
        private bool wasActivelyManaging = false; // Track if we were actively managing TDP

        // PID Tuning - conservative increase, aggressive decrease
        private const double Kp = 0.15;   // Lower proportional gain for gentler response
        private const double Ki = 0.02;   // Lower integral to avoid overshoot
        private const double Kd = 0.08;   // Good derivative for stability

        // Asymmetric response - slow to increase TDP, quick to decrease
        private const double IncreaseMultiplier = 0.5;  // Very conservative increase
        private const double DecreaseMultiplier = 1.0;  // Aggressive decrease to find minimum TDP

        // TDP limits (configurable from widget)
        private int minTDP = 4;
        private int maxTDP = 35;

        // Update intervals - sample FPS frequently but make TDP decisions less often
        private const double SampleIntervalMs = 250;   // 250ms FPS sampling for smoother data
        private const double DecisionIntervalMs = 500; // 500ms TDP decisions to respect hardware response time
        private const int SamplesPerDecision = 2;      // DecisionIntervalMs / SampleIntervalMs
        private int samplesSinceLastDecision = 0;      // Counter for samples since last TDP decision

        // Smoothing - 10 samples at 250ms = 2.5 seconds of history
        private double[] fpsHistory = new double[10];
        private int fpsHistoryIndex = 0;
        private int fpsHistoryCount = 0;

        // Trend detection - 5 deltas for better trend accuracy
        private double[] fpsDeltas = new double[5];
        private int fpsDeltaIndex = 0;
        private int fpsDeltaCount = 0;
        private double lastSmoothedFPS = 0;

        // Hysteresis - reduced requirements for decreasing TDP
        private const int StableReadingsRequired = 2;
        private const double DeadZone = 2.0;  // Tighter dead zone
        private const double UpperDeadZone = 3.0;  // Reduced threshold before decreasing TDP

        // FPS cap detection - probe for power savings when at target
        private const int StableAtTargetRequired = 6;
        private int consecutiveAtTarget = 0;
        private int lastProbeTDP = 0;  // TDP we dropped to during probing

        // Sweet spot detection - track TDP history to find optimal value
        private const int TDPHistorySize = 20;  // Track last 20 TDP readings
        private int[] tdpHistory = new int[TDPHistorySize];
        private int tdpHistoryIndex = 0;
        private int tdpHistoryCount = 0;
        private int sweetSpotTDP = 0;  // Detected optimal TDP
        private int sweetSpotConfidence = 0;  // How confident we are (0-100)
        private const int SweetSpotThreshold = 60;  // Confidence needed to use sweet spot

        // Diminishing returns tracking
        private double[] tdpIncreaseFPSGains = new double[4];  // Track FPS gain from last 4 TDP increases
        private int tdpGainIndex = 0;
        private int tdpGainCount = 0;
        private double lastFPSBeforeTDPIncrease = 0;  // FPS before most recent TDP increase
        private int lastTDPBeforeIncrease = 0;  // TDP before most recent increase
        private bool pendingTDPGainMeasurement = false;  // True when waiting to measure FPS gain

        // Ceiling detection - handles unreachable FPS targets
        private double achievableFPS = 0;          // EWMA estimate of max achievable FPS
        private double lockedAchievableFPS = 0;    // Locked ceiling FPS (doesn't drift down)
        private int effectiveTarget = 60;          // Adjusted target (min of user target and achievable)
        private bool isCeilingDetected = false;    // True when target is unreachable
        private int consecutiveCeilingFrames = 0;  // Counter for steady-state below target
        private int consecutiveHardCeilingSignals = 0;  // Counter for hard ceiling indicators (CPU-bound / diminishing returns)
        private int consecutiveAboveCeilingFrames = 0;  // Counter for hysteresis when exiting ceiling mode
        private const int CeilingDetectionThreshold = 12;
        private const int CeilingHardSignalHysteresis = 8;
        private const int CeilingExitHysteresis = 3;
        private const double CeilingGpuUtilThreshold = 95.0;
        private const double AchievableFPSAlpha = 0.15;
        private const int CeilingMargin = 3;
        private const double CeilingWarmupAfterGameStartMs = 12000;
        private const double CeilingWarmupAfterTargetChangeMs = 6000;

        // Power optimization mode - minimize TDP while maintaining FPS when ceiling hit
        private bool isPowerOptimizationMode = false;
        private int optimalCeilingTDP = 0;

        // OSD Status
        public string StatusText { get; private set; } = "";
        public string TrendText { get; private set; } = "";
        public int CurrentTDPValue { get; private set; } = 0;
        public int NewTDPValue { get; private set; } = 0;
        public bool IsProbing { get; private set; } = false;
        public int SweetSpotTDP => sweetSpotTDP;
        public int SweetSpotConfidence => sweetSpotConfidence;
        public bool IsCeilingDetected => isCeilingDetected;
        public int EffectiveTarget => effectiveTarget;
        public double AchievableFPS => achievableFPS;
        public bool IsPowerOptimizationMode => isPowerOptimizationMode;

        /// <summary>
        /// Gets the average FPS gained per watt from recent TDP increases.
        /// Returns -1 if no data available yet.
        /// </summary>
        public double GetAverageFpsPerWatt()
        {
            if (tdpGainCount == 0) return -1;
            double sum = 0;
            for (int i = 0; i < tdpGainCount; i++)
                sum += tdpIncreaseFPSGains[i];
            return sum / tdpGainCount;
        }

        public AutoTDPManager(PerformanceManager performanceManager, SystemManager systemManager) : base()
        {
            this.performanceManager = performanceManager;
            this.systemManager = systemManager;

            enabled = new AutoTDPEnabledProperty(false, this);
            targetFPS = new AutoTDPTargetFPSProperty(60, this);
            currentFPS = new AutoTDPCurrentFPSProperty(0, this);
            minTDPProperty = new AutoTDPMinTDPProperty(8, this);  // Default 8W
            maxTDPProperty = new AutoTDPMaxTDPProperty(30, this); // Default 30W
            pauseWhenUnfocused = new AutoTDPPauseWhenUnfocusedProperty(true, this);

            minTDP = minTDPProperty.Value;
            maxTDP = maxTDPProperty.Value;
            lastTargetFPS = targetFPS.Value;

            Logger.Info("AutoTDPManager initialized (PID mode)");
        }

        public void UpdateTDPLimits(int min, int max)
        {
            if (min < 4) min = 4;
            if (max > 85) max = 85;
            if (min > max) min = max;

            minTDP = min;
            maxTDP = max;

            if (lastAppliedTDP < minTDP) lastAppliedTDP = minTDP;
            if (lastAppliedTDP > maxTDP) lastAppliedTDP = maxTDP;

            ResetCeilingDetectionState(log: true, reason: "TDP limits changed");

            Logger.Info($"AutoTDP limits updated: Min={minTDP}W, Max={maxTDP}W");
        }

        public override void Update()
        {
            base.Update();

            if (!enabled.Value)
            {
                RestoreProfileTDPIfActive("AutoTDP disabled");
                wasActivelyManaging = false;

                if (integral != 0)
                {
                    ResetState();
                }
                if (performanceManager.TDP != null)
                {
                    lastAppliedTDP = performanceManager.TDP.Value;
                }
                performanceManager.IsAutoTDPActive = false;
                StatusText = "";
                TrendText = "";
                return;
            }

            // Check if a game is running
            var runningGame = systemManager.RunningGame.Value;
            if (!runningGame.IsValid())
            {
                RestoreProfileTDPIfActive("Game exited");
                wasActivelyManaging = false;

                if (currentGamePath != null)
                {
                    currentGamePath = null;
                    currentGameName = null;
                }

                if (currentFPS.Value != 0)
                {
                    currentFPS.SetValue(0);
                }
                ResetState();
                performanceManager.IsAutoTDPActive = false;
                StatusText = "Waiting for game";
                TrendText = "";
                return;
            }

            // Detect game change
            string newGamePath = runningGame.GameId.Path;
            string newGameName = runningGame.GameId.Name;
            if (newGamePath != currentGamePath)
            {
                currentGamePath = newGamePath;
                currentGameName = newGameName;
                gameStartTime = DateTime.Now;
                ResetCeilingDetectionState(log: true, reason: "new game detected");
                Logger.Info($"AutoTDP: New game detected - '{currentGameName}' at {currentGamePath}");

                // Seed lastAppliedTDP from current TDP value. A profile switch may have set TDP
                // (e.g., 25W) but lastAppliedTDP is stale.
                if (performanceManager.TDP != null)
                {
                    int actualTDP = performanceManager.TDP.Value;
                    if (actualTDP != lastAppliedTDP)
                    {
                        Logger.Info($"AutoTDP: New game, seeding TDP from current value: {actualTDP}W (was {lastAppliedTDP}W)");
                        lastAppliedTDP = actualTDP;
                    }
                }
            }

            // Pause AutoTDP if game is in background (unless the user disabled that guard)
            if (!runningGame.IsForeground && pauseWhenUnfocused.Value)
            {
                RestoreProfileTDPIfActive("Game not focused");
                wasActivelyManaging = false;
                performanceManager.IsAutoTDPActive = false;
                StatusText = "Game not focused";
                TrendText = "";
                return;
            }

            // AutoTDP can only manage TDP in Custom TDP mode
            if (!performanceManager.IsInCustomMode)
            {
                RestoreProfileTDPIfActive("Not in Custom mode");
                wasActivelyManaging = false;
                performanceManager.IsAutoTDPActive = false;
                ResetState();
                StatusText = "Not in Custom mode";
                TrendText = "";
                return;
            }

            // Now actively managing TDP - block other TDP-reapply paths from fighting us
            performanceManager.IsAutoTDPActive = true;
            if (!wasActivelyManaging && performanceManager.TDP != null)
            {
                int actualTDP = performanceManager.TDP.Value;
                if (actualTDP != lastAppliedTDP)
                {
                    Logger.Info($"AutoTDP: Starting active management, seeding TDP from current value: {actualTDP}W (was {lastAppliedTDP}W)");
                    lastAppliedTDP = actualTDP;
                }
            }
            wasActivelyManaging = true;

            // Rate limit FPS sampling (250ms intervals)
            var now = DateTime.Now;
            if (lastUpdateTime != DateTime.MinValue)
            {
                var elapsed = (now - lastUpdateTime).TotalMilliseconds;
                if (elapsed < SampleIntervalMs)
                {
                    return;
                }
            }
            lastUpdateTime = now;
            samplesSinceLastDecision++;

            // Get current FPS from RTSS
            int measuredFPS = GetCurrentFPS(runningGame.ProcessId);
            if (measuredFPS <= 0)
            {
                StatusText = "No FPS data";
                return;
            }

            if (currentFPS.Value != measuredFPS)
            {
                currentFPS.SetValue(measuredFPS);
            }

            // Detect target FPS changes and reset history to avoid stale data affecting decisions
            if (targetFPS.Value != lastTargetFPS)
            {
                Logger.Info($"AutoTDP: Target FPS changed from {lastTargetFPS} to {targetFPS.Value}, resetting FPS history");
                lastTargetFPS = targetFPS.Value;
                lastTargetChangeTime = DateTime.Now;
                ResetCeilingDetectionState(log: true, reason: "target FPS changed");
                fpsHistoryCount = 0;
                fpsHistoryIndex = 0;
                fpsDeltaCount = 0;
                fpsDeltaIndex = 0;
                lastSmoothedFPS = 0;
                consecutiveStableReadings = 0;
                consecutiveAtTarget = 0;
                StatusText = "Target changed";
                return;  // Skip this cycle to let things settle
            }

            // Spike detection - detect sudden FPS jumps (menus opening, pause screens)
            double currentAverage = CalculateWeightedAverage();
            bool recentTargetChange = (DateTime.Now - lastTargetChangeTime).TotalMilliseconds < TargetChangeSettleTimeMs;

            if (fpsHistoryCount >= 6 && currentAverage > 0 && !recentTargetChange)
            {
                double spikeThreshold = currentAverage * 1.5;
                if (measuredFPS > spikeThreshold && measuredFPS > currentAverage + 50)
                {
                    Logger.Debug($"AutoTDP: Spike detected - FPS={measuredFPS}, avg={currentAverage:F1} (likely menu, skipping)");
                    StatusText = "Spike detected";
                    return;
                }
            }

            // Add to history for smoothing
            fpsHistory[fpsHistoryIndex] = measuredFPS;
            fpsHistoryIndex = (fpsHistoryIndex + 1) % fpsHistory.Length;
            if (fpsHistoryCount < fpsHistory.Length)
            {
                fpsHistoryCount++;
            }

            double smoothedFPS = CalculateWeightedAverage();

            if (lastSmoothedFPS > 0)
            {
                double delta = smoothedFPS - lastSmoothedFPS;
                fpsDeltas[fpsDeltaIndex] = delta;
                fpsDeltaIndex = (fpsDeltaIndex + 1) % fpsDeltas.Length;
                if (fpsDeltaCount < fpsDeltas.Length)
                {
                    fpsDeltaCount++;
                }
            }
            lastSmoothedFPS = smoothedFPS;

            double trend = CalculateTrend();
            double predictedFPS = smoothedFPS + (trend * 2);

            if (Math.Abs(trend) < 0.5)
                TrendText = "Stable";
            else if (trend > 2)
                TrendText = "Rising++";
            else if (trend > 0)
                TrendText = "Rising";
            else if (trend < -2)
                TrendText = "Falling--";
            else
                TrendText = "Falling";

            // Use lastAppliedTDP as source of truth since SetAutoTDP debounces and TDP.Value may be stale
            int currentTDP = lastAppliedTDP;

            double gpuUtil = performanceManager.GPUUsage?.Value ?? 50.0;

            UpdateCeilingDetection(smoothedFPS, currentTDP, gpuUtil);

            bool shouldMakeDecision = samplesSinceLastDecision >= SamplesPerDecision;
            if (!shouldMakeDecision)
            {
                return;
            }
            samplesSinceLastDecision = 0;

            int newTDP = CalculateConservativeTDP(smoothedFPS, predictedFPS, trend, currentTDP, gpuUtil);

            CurrentTDPValue = currentTDP;
            NewTDPValue = newTDP;

            UpdateTDPHistory(newTDP);
            AnalyzeSweetSpot();

            if (newTDP != currentTDP)
            {
                if (newTDP > currentTDP)
                {
                    RecordTDPIncrease(smoothedFPS, currentTDP);
                }

                string action = newTDP > currentTDP ? "Increasing" : "Decreasing";
                string ceilingPrefix = isCeilingDetected ? "[Ceiling] " : "";
                string powerOptPrefix = isPowerOptimizationMode ? "[PwrOpt] " : "";
                StatusText = ceilingPrefix + powerOptPrefix + action;
                if (sweetSpotConfidence >= SweetSpotThreshold)
                {
                    StatusText += $" (sweet:{sweetSpotTDP}W)";
                }
                string ceilingInfo = isCeilingDetected ? $", Ceiling(eff={effectiveTarget}, achv≈{achievableFPS:F0})" : "";
                Logger.Info($"AutoTDP: FPS={measuredFPS} (smooth={smoothedFPS:F1}, pred={predictedFPS:F1}), Trend={trend:F2}, Target={targetFPS.Value}, TDP: {currentTDP}W -> {newTDP}W{ceilingInfo}, SweetSpot={sweetSpotTDP}W@{sweetSpotConfidence}%");
                performanceManager.SetAutoTDP(newTDP);
                lastAppliedTDP = newTDP;
                consecutiveStableReadings = 0;
            }
            else
            {
                double error = effectiveTarget - smoothedFPS;
                double userError = targetFPS.Value - smoothedFPS;
                string ceilingPrefix = isCeilingDetected ? "[Ceiling] " : "";
                string powerOptPrefix = isPowerOptimizationMode ? "[PwrOpt] " : "";

                if (Math.Abs(error) <= DeadZone)
                {
                    if (isPowerOptimizationMode)
                    {
                        StatusText = $"{ceilingPrefix}Optimized {currentTDP}W";
                    }
                    else if (isCeilingDetected)
                    {
                        StatusText = $"{ceilingPrefix}At limit ({achievableFPS:F0} FPS)";
                    }
                    else if (sweetSpotConfidence >= SweetSpotThreshold)
                    {
                        StatusText = $"Locked {sweetSpotTDP}W";
                    }
                    else
                    {
                        StatusText = "On target";
                    }
                    consecutiveStableReadings++;
                }
                else if (error > 0)
                {
                    if (isCeilingDetected && userError > DeadZone)
                    {
                        StatusText = $"{ceilingPrefix}Target unreachable";
                    }
                    else
                    {
                        StatusText = ceilingPrefix + powerOptPrefix + "Below target";
                    }
                }
                else
                {
                    StatusText = ceilingPrefix + powerOptPrefix + "Above target";
                    consecutiveStableReadings++;
                }
            }
        }

        /// <summary>
        /// Restores the profile's TDP whenever AutoTDP stops actively managing it (disabled,
        /// game exited, lost focus, or left Custom mode).
        /// </summary>
        private void RestoreProfileTDPIfActive(string reason)
        {
            if (wasActivelyManaging && performanceManager.TDP != null)
            {
                int profileTDP = performanceManager.TDP.Value;
                Logger.Info($"AutoTDP: {reason} - restoring profile TDP: {profileTDP}W");
                performanceManager.SetAutoTDP(profileTDP);
            }
        }

        private void UpdateTDPHistory(int tdp)
        {
            tdpHistory[tdpHistoryIndex] = tdp;
            tdpHistoryIndex = (tdpHistoryIndex + 1) % TDPHistorySize;
            if (tdpHistoryCount < TDPHistorySize)
            {
                tdpHistoryCount++;
            }
        }

        private void AnalyzeSweetSpot()
        {
            if (tdpHistoryCount < 10) return;

            var tdpCounts = new Dictionary<int, int>();
            int minSeen = int.MaxValue;
            int maxSeen = int.MinValue;

            for (int i = 0; i < tdpHistoryCount; i++)
            {
                int tdp = tdpHistory[i];
                if (!tdpCounts.ContainsKey(tdp))
                    tdpCounts[tdp] = 0;
                tdpCounts[tdp]++;
                minSeen = Math.Min(minSeen, tdp);
                maxSeen = Math.Max(maxSeen, tdp);
            }

            int mostCommonTDP = 0;
            int mostCommonCount = 0;
            foreach (var kvp in tdpCounts)
            {
                if (kvp.Value > mostCommonCount)
                {
                    mostCommonCount = kvp.Value;
                    mostCommonTDP = kvp.Key;
                }
            }

            int range = maxSeen - minSeen;
            double frequencyScore = (double)mostCommonCount / tdpHistoryCount * 100;
            double stabilityScore = range <= 2 ? 100 : range <= 4 ? 75 : range <= 6 ? 50 : 25;

            int adjacentCount = mostCommonCount;
            if (tdpCounts.ContainsKey(mostCommonTDP - 1))
                adjacentCount += tdpCounts[mostCommonTDP - 1];
            if (tdpCounts.ContainsKey(mostCommonTDP + 1))
                adjacentCount += tdpCounts[mostCommonTDP + 1];
            double adjacentScore = (double)adjacentCount / tdpHistoryCount * 100;

            sweetSpotConfidence = (int)((frequencyScore * 0.3 + stabilityScore * 0.3 + adjacentScore * 0.4));
            sweetSpotTDP = mostCommonTDP;

            if (sweetSpotConfidence >= SweetSpotThreshold)
            {
                Logger.Debug($"AutoTDP: Sweet spot detected at {sweetSpotTDP}W with {sweetSpotConfidence}% confidence (range={range}, freq={frequencyScore:F0}%, adjacent={adjacentScore:F0}%)");
            }
        }

        private double CalculateWeightedAverage()
        {
            if (fpsHistoryCount == 0) return 0;

            double weightedSum = 0;
            double totalWeight = 0;

            for (int i = 0; i < fpsHistoryCount; i++)
            {
                int age = (fpsHistoryIndex - 1 - i + fpsHistory.Length) % fpsHistory.Length;
                if (age >= fpsHistoryCount) continue;

                double weight = fpsHistoryCount - age;
                weightedSum += fpsHistory[(fpsHistoryIndex - 1 - i + fpsHistory.Length) % fpsHistory.Length] * weight;
                totalWeight += weight;
            }

            return totalWeight > 0 ? weightedSum / totalWeight : 0;
        }

        private double CalculateTrend()
        {
            if (fpsDeltaCount == 0) return 0;

            double sum = 0;
            for (int i = 0; i < fpsDeltaCount; i++)
            {
                sum += fpsDeltas[i];
            }
            return sum / fpsDeltaCount;
        }

        private int GetCurrentFPS(int processId)
        {
            try
            {
                var appEntries = OSD.GetAppEntries(AppFlags.MASK);
                var entry = appEntries?.FirstOrDefault(e => e.ProcessId == processId);
                if (entry != null)
                {
                    return (int)entry.InstantaneousFrames;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"AutoTDP: Error getting FPS from RTSS: {ex.Message}");
            }
            return 0;
        }

        private int CalculateConservativeTDP(double smoothedFPS, double predictedFPS, double trend, int currentTDP, double gpuUtil)
        {
            // Use effective target when ceiling is detected, otherwise use user target
            int target = isCeilingDetected ? effectiveTarget : targetFPS.Value;
            double error = target - smoothedFPS;
            double predictedError = target - predictedFPS;

            // Power optimization mode: minimize TDP while maintaining achievable FPS
            if (isPowerOptimizationMode && isCeilingDetected)
            {
                double achError = achievableFPS - smoothedFPS;

                if (Math.Abs(achError) <= 2.0 && currentTDP > minTDP)
                {
                    if (gpuUtil < 90)
                    {
                        Logger.Debug($"AutoTDP [PwrOpt]: GPU={gpuUtil:F0}%, trying to reduce TDP from {currentTDP}W");
                        return currentTDP - 1;
                    }
                }
                else if (achError > 3.0 && currentTDP < optimalCeilingTDP)
                {
                    Logger.Debug($"AutoTDP [PwrOpt]: FPS dropped ({smoothedFPS:F1} < {achievableFPS:F1}), restoring TDP");
                    return Math.Min(maxTDP, currentTDP + 1);
                }

                if (Math.Abs(achError) <= 1.5 && currentTDP < optimalCeilingTDP)
                {
                    optimalCeilingTDP = currentTDP;
                    Logger.Debug($"AutoTDP [PwrOpt]: New optimal ceiling TDP: {optimalCeilingTDP}W");
                }

                if (Math.Abs(achError) <= 3.0)
                {
                    return currentTDP;
                }
            }

            bool atOrAboveTarget = error <= 1.0 && Math.Abs(trend) < 1.0;

            double effectiveError = error;
            if (predictedError > error)
            {
                effectiveError = (error + predictedError) / 2;
            }

            bool inDeadZone = Math.Abs(error) <= DeadZone;
            bool inUpperDeadZone = error < -UpperDeadZone;

            if (trend < -1.0 && error > -DeadZone)
            {
                effectiveError = Math.Max(effectiveError, DeadZone + 1);
                if (IsProbing)
                {
                    Logger.Info($"AutoTDP: FPS dropping during probe, canceling probe");
                    IsProbing = false;
                    consecutiveAtTarget = 0;
                }
            }

            // Handle FPS cap probing - try to find minimum TDP needed
            if (atOrAboveTarget && !IsProbing)
            {
                consecutiveAtTarget++;
                Logger.Debug($"AutoTDP: At/above target ({smoothedFPS:F1}/{target}), consecutiveAtTarget={consecutiveAtTarget}/{StableAtTargetRequired}");
                if (consecutiveAtTarget >= StableAtTargetRequired && currentTDP > minTDP)
                {
                    IsProbing = true;
                    lastProbeTDP = currentTDP;
                    int probeTDP = currentTDP - 1;
                    Logger.Info($"AutoTDP: Stable at/above target, probing lower TDP: {currentTDP}W -> {probeTDP}W");
                    consecutiveAtTarget = 0;
                    return probeTDP;
                }
            }
            else if (IsProbing)
            {
                if (error > 2.0)
                {
                    Logger.Info($"AutoTDP: Probe failed (FPS dropped to {smoothedFPS:F1}), restoring TDP: {currentTDP}W -> {lastProbeTDP}W");
                    IsProbing = false;
                    consecutiveAtTarget = 0;
                    return lastProbeTDP;
                }
                else if (error <= 1.0)
                {
                    consecutiveAtTarget++;
                    Logger.Debug($"AutoTDP: Probing, FPS at {smoothedFPS:F1}/{target}, consecutiveAtTarget={consecutiveAtTarget}/{StableAtTargetRequired / 2}");
                    if (consecutiveAtTarget >= StableAtTargetRequired / 2)
                    {
                        Logger.Info($"AutoTDP: Probe succeeded at {currentTDP}W, will probe again");
                        lastProbeTDP = currentTDP;
                        IsProbing = false;
                        consecutiveAtTarget = StableAtTargetRequired - 2;
                    }
                    return currentTDP;
                }
            }
            else if (!atOrAboveTarget)
            {
                if (consecutiveAtTarget > 0)
                {
                    Logger.Debug($"AutoTDP: Below target (FPS={smoothedFPS:F1}, target={target}, trend={trend:F2}), resetting counter");
                }
                consecutiveAtTarget = 0;
            }

            if (inDeadZone && consecutiveStableReadings >= StableReadingsRequired)
            {
                integral *= 0.95;
                return currentTDP;
            }

            double dt = SampleIntervalMs / 1000.0;

            double P = Kp * effectiveError;

            integral += effectiveError * dt;
            integral = Math.Max(-30, Math.Min(30, integral));
            double I = Ki * integral;

            double D = -Kd * trend;

            double adjustment = P + I + D;

            if (adjustment > 0)
            {
                adjustment *= IncreaseMultiplier;
                if (IsProbing)
                {
                    IsProbing = false;
                    consecutiveAtTarget = 0;
                }
            }
            else
            {
                if (error >= 0 || consecutiveStableReadings < 1)
                {
                    adjustment = 0;
                }
                else
                {
                    adjustment *= DecreaseMultiplier;
                    double maxDecrease = inUpperDeadZone ? -2.0 : -1.0;
                    adjustment = Math.Max(adjustment, maxDecrease);
                }
            }

            int newTDP = (int)Math.Round(currentTDP + adjustment);

            int maxIncrease = 1;
            int maxDecreaseCycle = inUpperDeadZone ? 2 : 1;
            newTDP = Math.Max(currentTDP - maxDecreaseCycle, Math.Min(currentTDP + maxIncrease, newTDP));

            newTDP = Math.Max(minTDP, Math.Min(maxTDP, newTDP));

            Logger.Debug($"AutoTDP: Err={error:F1}, PredErr={predictedError:F1}, Trend={trend:F2}, P={P:F2}, I={I:F2}, D={D:F2}, Adj={adjustment:F2}, Stable={consecutiveStableReadings}, AtTarget={consecutiveAtTarget}");

            return newTDP;
        }

        private void ResetState()
        {
            integral = 0;
            fpsHistoryCount = 0;
            fpsHistoryIndex = 0;
            fpsDeltaCount = 0;
            fpsDeltaIndex = 0;
            lastSmoothedFPS = 0;
            consecutiveStableReadings = 0;
            consecutiveAtTarget = 0;
            IsProbing = false;
            lastProbeTDP = 0;
            lastUpdateTime = DateTime.MinValue;
            samplesSinceLastDecision = 0;
            tdpHistoryCount = 0;
            tdpHistoryIndex = 0;
            sweetSpotTDP = 0;
            sweetSpotConfidence = 0;
            ResetCeilingDetectionState();
            tdpGainCount = 0;
            tdpGainIndex = 0;
            pendingTDPGainMeasurement = false;
            Logger.Debug("AutoTDP: State reset");
        }

        private void ResetCeilingDetectionState(bool log = false, string reason = null)
        {
            bool hadCeilingState =
                isCeilingDetected ||
                isPowerOptimizationMode ||
                effectiveTarget != targetFPS.Value ||
                consecutiveCeilingFrames > 0 ||
                consecutiveHardCeilingSignals > 0;

            achievableFPS = 0;
            lockedAchievableFPS = 0;
            effectiveTarget = targetFPS.Value;
            isCeilingDetected = false;
            consecutiveCeilingFrames = 0;
            consecutiveHardCeilingSignals = 0;
            consecutiveAboveCeilingFrames = 0;
            isPowerOptimizationMode = false;
            optimalCeilingTDP = 0;

            if (log && hadCeilingState)
            {
                string suffix = string.IsNullOrEmpty(reason) ? string.Empty : $" ({reason})";
                Logger.Info($"AutoTDP: Ceiling state reset{suffix}. User target={targetFPS.Value}");
            }
        }

        private bool IsCeilingWarmupActive()
        {
            DateTime now = DateTime.Now;

            if (gameStartTime != DateTime.MinValue &&
                (now - gameStartTime).TotalMilliseconds < CeilingWarmupAfterGameStartMs)
            {
                return true;
            }

            if (lastTargetChangeTime != DateTime.MinValue &&
                (now - lastTargetChangeTime).TotalMilliseconds < CeilingWarmupAfterTargetChangeMs)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Detects and handles unreachable FPS targets (ceiling detection).
        /// </summary>
        private void UpdateCeilingDetection(double smoothedFPS, int currentTDP, double gpuUtil)
        {
            int userTarget = targetFPS.Value;
            double fpsError = userTarget - smoothedFPS;
            bool atMaxTDP = currentTDP >= maxTDP - 1;
            bool gpuNotSaturated = gpuUtil < CeilingGpuUtilThreshold;

            if (pendingTDPGainMeasurement && fpsHistoryCount >= 3)
            {
                double fpsGain = smoothedFPS - lastFPSBeforeTDPIncrease;
                int tdpIncrease = currentTDP - lastTDPBeforeIncrease;

                if (tdpIncrease > 0)
                {
                    double gainPerWatt = fpsGain / tdpIncrease;
                    tdpIncreaseFPSGains[tdpGainIndex] = gainPerWatt;
                    tdpGainIndex = (tdpGainIndex + 1) % tdpIncreaseFPSGains.Length;
                    if (tdpGainCount < tdpIncreaseFPSGains.Length)
                        tdpGainCount++;

                    Logger.Debug($"AutoTDP Ceiling: TDP +{tdpIncrease}W yielded {fpsGain:F1} FPS ({gainPerWatt:F2} FPS/W)");
                }
                pendingTDPGainMeasurement = false;
            }

            bool ceilingConditionMet = false;
            bool hardCeilingSignal = false;

            if (fpsError <= 0)
            {
                consecutiveCeilingFrames = 0;
                consecutiveHardCeilingSignals = 0;
            }
            else
            {
                if (atMaxTDP && gpuNotSaturated && fpsError > DeadZone)
                {
                    hardCeilingSignal = true;
                    Logger.Debug($"AutoTDP Ceiling: CPU-bound detected (GPU={gpuUtil:F0}% at max TDP={maxTDP}W, FPS error={fpsError:F1})");
                }

                if (atMaxTDP && fpsError > DeadZone && tdpGainCount >= 3)
                {
                    double avgGainPerWatt = 0;
                    for (int i = 0; i < tdpGainCount; i++)
                        avgGainPerWatt += tdpIncreaseFPSGains[i];
                    avgGainPerWatt /= tdpGainCount;

                    if (avgGainPerWatt < 0.3)
                    {
                        hardCeilingSignal = true;
                        Logger.Debug($"AutoTDP Ceiling: Diminishing returns at {currentTDP}W (avg {avgGainPerWatt:F2} FPS/W)");
                    }
                }

                if (fpsError > DeadZone && atMaxTDP)
                {
                    consecutiveCeilingFrames++;
                }
                else
                {
                    consecutiveCeilingFrames = 0;
                }

                if (hardCeilingSignal)
                {
                    consecutiveHardCeilingSignals++;
                }
                else
                {
                    consecutiveHardCeilingSignals = 0;
                }
            }

            if (!isCeilingDetected && IsCeilingWarmupActive())
            {
                if (consecutiveCeilingFrames > 0 || consecutiveHardCeilingSignals > 0)
                {
                    Logger.Debug($"AutoTDP Ceiling: warmup active, deferring ceiling detection (steady={consecutiveCeilingFrames}, hard={consecutiveHardCeilingSignals})");
                }
                consecutiveCeilingFrames = 0;
                consecutiveHardCeilingSignals = 0;
                return;
            }

            if (consecutiveHardCeilingSignals >= CeilingHardSignalHysteresis)
            {
                ceilingConditionMet = true;
                Logger.Debug($"AutoTDP Ceiling: Hard ceiling hysteresis met for {consecutiveHardCeilingSignals} frames");
            }

            if (consecutiveCeilingFrames >= CeilingDetectionThreshold)
            {
                ceilingConditionMet = true;
                Logger.Debug($"AutoTDP Ceiling: Steady-state below target for {consecutiveCeilingFrames} frames");
            }

            if (ceilingConditionMet && !isCeilingDetected)
            {
                isCeilingDetected = true;
                achievableFPS = smoothedFPS;
                lockedAchievableFPS = smoothedFPS;
                effectiveTarget = Math.Max(30, (int)(smoothedFPS - CeilingMargin));
                optimalCeilingTDP = currentTDP;
                consecutiveAboveCeilingFrames = 0;
                consecutiveHardCeilingSignals = 0;
                Logger.Info($"AutoTDP: Ceiling detected! User target={userTarget}, achievable FPS≈{achievableFPS:F0}, effective target={effectiveTarget}");
            }
            else if (isCeilingDetected)
            {
                double newAchievable = (AchievableFPSAlpha * smoothedFPS) + ((1 - AchievableFPSAlpha) * achievableFPS);
                if (newAchievable > achievableFPS)
                {
                    achievableFPS = newAchievable;
                    if (achievableFPS > lockedAchievableFPS)
                    {
                        lockedAchievableFPS = achievableFPS;
                        Logger.Debug($"AutoTDP Ceiling: Updated locked achievable FPS to {lockedAchievableFPS:F0}");
                    }
                }
                double effectiveAchievable = lockedAchievableFPS;

                int newEffectiveTarget = Math.Max(30, (int)(effectiveAchievable - CeilingMargin));
                if (newEffectiveTarget > effectiveTarget)
                {
                    effectiveTarget = newEffectiveTarget;
                    Logger.Debug($"AutoTDP Ceiling: Updated effective target to {effectiveTarget} (achievable≈{effectiveAchievable:F0})");
                }

                double errorFromAchievable = effectiveAchievable - smoothedFPS;
                if (Math.Abs(errorFromAchievable) <= 2.0 && !isPowerOptimizationMode)
                {
                    isPowerOptimizationMode = true;
                    optimalCeilingTDP = currentTDP;
                    Logger.Info($"AutoTDP: Entering power optimization mode at {currentTDP}W (FPS≈{smoothedFPS:F0})");
                }

                if (smoothedFPS >= userTarget - DeadZone)
                {
                    consecutiveAboveCeilingFrames++;
                    if (consecutiveAboveCeilingFrames >= CeilingExitHysteresis)
                    {
                        Logger.Info($"AutoTDP: Target now reachable (sustained {consecutiveAboveCeilingFrames} frames), exiting ceiling mode");
                        ResetCeilingDetectionState();
                    }
                }
                else
                {
                    consecutiveAboveCeilingFrames = 0;
                }
            }
        }

        /// <summary>
        /// Records a TDP increase for diminishing returns tracking.
        /// </summary>
        private void RecordTDPIncrease(double currentFPS, int currentTDP)
        {
            lastFPSBeforeTDPIncrease = currentFPS;
            lastTDPBeforeIncrease = currentTDP;
            pendingTDPGainMeasurement = true;
        }
    }
}
