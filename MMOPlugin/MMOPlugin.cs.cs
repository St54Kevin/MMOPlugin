using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GameReaderCommon;
using SimHub.Plugins;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace MMOPlugin
{
    [PluginName("MMO Plugin")]
    [PluginAuthor("Kevin_Karas")]
    [PluginDescription("Extended Le Mans Ultimate standings data.")]
    public class MMOPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string PluginVersion = "0.1.1";

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle
        {
            get { return "MMO Plugin"; }
        }

        public ImageSource PictureIcon
        {
            get { return null; }
        }

        private const int MaxVehicles = 62;

        private readonly LMUSharedMemory _sharedMemory =
            new LMUSharedMemory();

        private readonly LMUStandingsApi _standingsApi =
            new LMUStandingsApi();

        private readonly LMUVehicleScoring[] _vehiclesByPosition =
            new LMUVehicleScoring[MaxVehicles];

        private readonly bool[] _hasVehicle =
            new bool[MaxVehicles];

        private LMUScoringInfo _scoringInfo;

        private bool _hasScoringInfo;

        private readonly string[] _relativeAhead = new string[3];
        private readonly string[] _relativeBehind = new string[3];
        private readonly int[] _incidentsByPosition = new int[MaxVehicles];
        private readonly string[][] _tyreCompoundsByPosition =
            new string[MaxVehicles][];
        private readonly Dictionary<int, LastLapTimingState> _lastLapTimingByVehicleId =
            new Dictionary<int, LastLapTimingState>();
        private int _playerIncidents = -1;

        private const int MaxMappedVehicles = 104;

        private sealed class LastLapTimingState
        {
            public bool HasLapStart;
            public double LapStartET;
            public double LastLapTime;
        }

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;

            _standingsApi.Start();
            ClearTrackLimitPoints();

            this.AttachDelegate(
                name: "MMO_ConnectionStatus",
                valueProvider: () =>
                    _sharedMemory.IsConnected
                        ? "Connected to LMU"
                        : "Waiting for LMU"
            );

            this.AttachDelegate(
                name: "MMO_TrackName",
                valueProvider: () =>
                    _hasScoringInfo
                        ? DecodeString(_scoringInfo.mTrackName)
                        : ""
            );

            this.AttachDelegate(
                name: "MMO_VehicleCount",
                valueProvider: () =>
                    _hasScoringInfo
                        ? _scoringInfo.mNumVehicles
                        : 0
            );

            this.AttachDelegate(
                name: "MMO_ApiConnectionStatus",
                valueProvider: () =>
                    _standingsApi.IsConnected
                        ? "Connected to LMU API"
                        : "Waiting for LMU API"
            );

            this.AttachDelegate(
                name: "MMO_ApiLastError",
                valueProvider: () =>
                    _standingsApi.LastError
            );

            this.AttachDelegate(
                name: "MMO_InvalidLap",
                valueProvider: () =>
                {
                    for (int positionIndex = 0;
                         positionIndex < MaxVehicles;
                         positionIndex++)
                    {
                        if (!_hasVehicle[positionIndex])
                            continue;

                        LMUVehicleScoring vehicle =
                            _vehiclesByPosition[positionIndex];

                        if (!vehicle.mIsPlayer)
                            continue;

                        return vehicle.mLastLapTime < 0.0 ? 1 : 0;
                    }

                    return 0;
                }
            );

            this.AttachDelegate(
                name: "MMO_PlayerInc",
                valueProvider: () => _playerIncidents
            );

            // -1 means the session's configured track-limit threshold is not available
            // through the shared-memory fields currently mapped by this plugin.
            this.AttachDelegate(
                name: "MMO_maxIncidents",
                valueProvider: () => -1
            );

            this.AttachDelegate(
                name: "MMO_RelativeAhead1",
                valueProvider: () => _relativeAhead[0]
            );

            this.AttachDelegate(
                name: "MMO_RelativeAhead2",
                valueProvider: () => _relativeAhead[1]
            );

            this.AttachDelegate(
                name: "MMO_RelativeAhead3",
                valueProvider: () => _relativeAhead[2]
            );

            this.AttachDelegate(
                name: "MMO_RelativeBehind1",
                valueProvider: () => _relativeBehind[0]
            );

            this.AttachDelegate(
                name: "MMO_RelativeBehind2",
                valueProvider: () => _relativeBehind[1]
            );

            this.AttachDelegate(
                name: "MMO_RelativeBehind3",
                valueProvider: () => _relativeBehind[2]
            );

            for (int position = 1;
                 position <= MaxVehicles;
                 position++)
            {
                int capturedPosition = position;

                string positionName =
                    capturedPosition.ToString(CultureInfo.InvariantCulture);

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Overall",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.Position
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Class",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.PositionClass
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "CarNumber",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.CarNumber ?? ""
                            : "";
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "CarClass",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.CarClass ?? ""
                            : "";
                    }
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompFL",
                    valueProvider: () => GetTyreCompound(capturedPosition, 0)
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompFR",
                    valueProvider: () => GetTyreCompound(capturedPosition, 1)
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompRL",
                    valueProvider: () => GetTyreCompound(capturedPosition, 2)
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompRR",
                    valueProvider: () => GetTyreCompound(capturedPosition, 3)
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "DriverName",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.DriverName ?? ""
                            : "";
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "TeamName",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.TeamName ?? ""
                            : "";
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "BestLaptime",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.BestLapTime
                            : 0.0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "LastLaptime",
                    valueProvider: () =>
                    {
                        int positionIndex = capturedPosition - 1;

                        if (positionIndex < 0 ||
                            positionIndex >= MaxVehicles ||
                            !_hasVehicle[positionIndex])
                        {
                            return FormatLapTime(0.0);
                        }

                        LMUVehicleScoring scoringVehicle =
                            _vehiclesByPosition[positionIndex];

                        double validLastLap = scoringVehicle.mLastLapTime;

                        if (validLastLap > 0.0 &&
                            !double.IsNaN(validLastLap) &&
                            !double.IsInfinity(validLastLap))
                        {
                            return FormatLapTime(validLastLap);
                        }

                        LastLapTimingState timingState;

                        if (_lastLapTimingByVehicleId.TryGetValue(
                                scoringVehicle.mID,
                                out timingState) &&
                            timingState.LastLapTime > 0.0 &&
                            !double.IsNaN(timingState.LastLapTime) &&
                            !double.IsInfinity(timingState.LastLapTime))
                        {
                            return FormatLapTime(timingState.LastLapTime);
                        }

                        LMUApiVehicle apiVehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        double apiLastLap = apiVehicle != null
                            ? apiVehicle.LastLapTime
                            : 0.0;

                        if (apiLastLap > 0.0 &&
                            !double.IsNaN(apiLastLap) &&
                            !double.IsInfinity(apiLastLap))
                        {
                            return FormatLapTime(apiLastLap);
                        }

                        return FormatLapTime(0.0);
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "GapToLeader",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "";

                        int lapsBehindClassLeader =
                            GetLapsBehindClassLeader(
                                capturedPosition,
                                vehicle
                            );

                        return FormatGap(
                            vehicle.GapToLeader,
                            lapsBehindClassLeader
                        );
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "IntervalToNext",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "";

                        int lapsBehindNext =
                            GetLapsBehindNext(
                                capturedPosition
                            );

                        return FormatGap(
                            vehicle.IntervalToNext,
                            lapsBehindNext
                        );
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "PitStatus",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "onTrack";

                        if (vehicle.InGarageStall)
                            return "InGarage";

                        if (vehicle.Pitting)
                            return "InPit";

                        if (!string.IsNullOrEmpty(
                                vehicle.PitState) &&
                            !string.Equals(
                                vehicle.PitState,
                                "NONE",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return "inPitLane";
                        }

                        return "onTrack";
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "VirtualEnergy",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "";

                        return FormatVirtualEnergy(
                            vehicle.VeFraction,
                            vehicle.FuelFraction
                        );
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Penalties",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.Penalties
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Incidents",
                    valueProvider: () =>
                        _incidentsByPosition[capturedPosition - 1]
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "InvalidLap",
                    valueProvider: () =>
                    {
                        int positionIndex = capturedPosition - 1;

                        if (positionIndex < 0 ||
                            positionIndex >= MaxVehicles ||
                            !_hasVehicle[positionIndex])
                        {
                            return 0;
                        }

                        return _vehiclesByPosition[positionIndex].mLastLapTime < 0.0
                            ? 1
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "FinishStatus",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null &&
                               !string.IsNullOrEmpty(vehicle.FinishStatus) &&
                               !string.Equals(
                                   vehicle.FinishStatus,
                                   "FSTAT_NONE",
                                   StringComparison.OrdinalIgnoreCase
                               )
                            ? 1
                            : 0;
                    }
                );
            }
        }

        public Control GetWPFSettingsControl(
            PluginManager pluginManager)
        {
            return new MMOPluginSettingsControl(
                pluginManager,
                PluginVersion,
                BuildPropertyNames()
            );
        }

        private static List<string> BuildPropertyNames()
        {
            List<string> names = new List<string>
            {
                "MMO_ConnectionStatus",
                "MMO_TrackName",
                "MMO_VehicleCount",
                "MMO_ApiConnectionStatus",
                "MMO_ApiLastError",
                "MMO_InvalidLap",
                "MMO_PlayerInc",
                "MMO_maxIncidents",
                "MMO_RelativeAhead1",
                "MMO_RelativeAhead2",
                "MMO_RelativeAhead3",
                "MMO_RelativeBehind1",
                "MMO_RelativeBehind2",
                "MMO_RelativeBehind3"
            };

            string[] positionSuffixes =
            {
                "Overall",
                "Class",
                "CarNumber",
                "CarClass",
                "TyreCompFL",
                "TyreCompFR",
                "TyreCompRL",
                "TyreCompRR",
                "DriverName",
                "TeamName",
                "BestLaptime",
                "LastLaptime",
                "GapToLeader",
                "IntervalToNext",
                "PitStatus",
                "VirtualEnergy",
                "Penalties",
                "Incidents",
                "InvalidLap",
                "FinishStatus"
            };

            for (int position = 1;
                 position <= MaxVehicles;
                 position++)
            {
                string positionName =
                    position.ToString(CultureInfo.InvariantCulture);

                foreach (string suffix in positionSuffixes)
                {
                    names.Add(
                        "MMO_Position" +
                        positionName +
                        suffix
                    );
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public void DataUpdate(
            PluginManager pluginManager,
            ref GameData data)
        {
            _sharedMemory.UpdateConnection();

            if (!_sharedMemory.IsConnected)
            {
                ClearRelativeGaps();
                ClearTrackLimitPoints();
                return;
            }

            _hasScoringInfo =
                _sharedMemory.TryReadScoringInfo(
                    out _scoringInfo
                );

            if (!_hasScoringInfo)
            {
                ClearRelativeGaps();
                ClearTrackLimitPoints();
                return;
            }

            for (int i = 0;
                 i < MaxVehicles;
                 i++)
            {
                _hasVehicle[i] = false;

                _vehiclesByPosition[i] =
                    new LMUVehicleScoring();
            }

            if (!_hasScoringInfo)
                return;

            _standingsApi.SetSession(
                _scoringInfo.mSession
            );

            int vehicleCount =
                _scoringInfo.mNumVehicles;

            if (vehicleCount < 0)
                vehicleCount = 0;

            if (vehicleCount > MaxVehicles)
                vehicleCount = MaxVehicles;

            for (int vehicleIndex = 0;
                 vehicleIndex < vehicleCount;
                 vehicleIndex++)
            {
                LMUVehicleScoring vehicle;

                if (!_sharedMemory.TryReadVehicleScoring(
                        vehicleIndex,
                        out vehicle))
                {
                    continue;
                }

                int place =
                    vehicle.mPlace;

                if (place < 1 ||
                    place > MaxVehicles)
                {
                    continue;
                }

                int positionIndex =
                    place - 1;

                _vehiclesByPosition[positionIndex] =
                    vehicle;

                _hasVehicle[positionIndex] =
                    true;
            }

            UpdateRelativeGaps(vehicleCount);
            UpdateTrackLimitPoints();
            UpdateLastLapTimes();
        }

        private void UpdateLastLapTimes()
        {
            for (int positionIndex = 0;
                 positionIndex < MaxVehicles;
                 positionIndex++)
            {
                if (!_hasVehicle[positionIndex])
                    continue;

                LMUVehicleScoring vehicle =
                    _vehiclesByPosition[positionIndex];

                if (vehicle.mID < 0)
                    continue;

                double currentLapStartET = vehicle.mLapStartET;

                if (double.IsNaN(currentLapStartET) ||
                    double.IsInfinity(currentLapStartET))
                {
                    continue;
                }

                LastLapTimingState state;

                if (!_lastLapTimingByVehicleId.TryGetValue(
                        vehicle.mID,
                        out state))
                {
                    state = new LastLapTimingState();
                    _lastLapTimingByVehicleId.Add(
                        vehicle.mID,
                        state
                    );
                }

                if (currentLapStartET <= 0.0)
                {
                    state.HasLapStart = false;
                    state.LapStartET = currentLapStartET;
                    state.LastLapTime = 0.0;
                    continue;
                }

                if (!state.HasLapStart)
                {
                    state.LapStartET = currentLapStartET;
                    state.HasLapStart = true;
                    continue;
                }

                if (currentLapStartET > state.LapStartET + 0.001)
                {
                    double completedLapTime =
                        currentLapStartET - state.LapStartET;

                    if (completedLapTime > 0.0 &&
                        completedLapTime <= 3600.0)
                    {
                        state.LastLapTime = completedLapTime;
                    }

                    state.LapStartET = currentLapStartET;
                }
                else if (currentLapStartET < state.LapStartET - 0.001)
                {
                    // Session reset or a vehicle slot/lap timer reset.
                    state.LapStartET = currentLapStartET;
                    state.LastLapTime = 0.0;
                    state.HasLapStart = true;
                }
            }
        }

        private void UpdateTrackLimitPoints()
        {
            ClearTrackLimitPoints();

            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById =
                new Dictionary<int, LMUVehicleTelemetryRelative>();

            for (int telemetryIndex = 0; telemetryIndex < 104; telemetryIndex++)
            {
                LMUVehicleTelemetryRelative telemetry;

                if (!_sharedMemory.TryReadVehicleTelemetryRelative(
                        telemetryIndex,
                        out telemetry))
                {
                    continue;
                }

                if (telemetry.mID >= 0 &&
                    !telemetryById.ContainsKey(telemetry.mID))
                {
                    telemetryById.Add(telemetry.mID, telemetry);
                }
            }

            for (int positionIndex = 0; positionIndex < MaxVehicles; positionIndex++)
            {
                if (!_hasVehicle[positionIndex])
                    continue;

                LMUVehicleScoring vehicle = _vehiclesByPosition[positionIndex];
                LMUVehicleTelemetryRelative telemetry;

                if (!telemetryById.TryGetValue(vehicle.mID, out telemetry))
                    continue;

                // LMU exposes normalized track-limit steps in this telemetry byte.
                // Keep the raw value for in-game comparison; scale can be confirmed empirically.
                _incidentsByPosition[positionIndex] = telemetry.mTrackLimitsSteps;

                _tyreCompoundsByPosition[positionIndex] = new[]
                {
                    FormatTyreCompound(telemetry.mTyreCompFL),
                    FormatTyreCompound(telemetry.mTyreCompFR),
                    FormatTyreCompound(telemetry.mTyreCompRL),
                    FormatTyreCompound(telemetry.mTyreCompRR)
                };

                if (vehicle.mIsPlayer)
                    _playerIncidents = telemetry.mTrackLimitsSteps;
            }
        }

        private void ClearTrackLimitPoints()
        {
            _playerIncidents = -1;

            for (int i = 0; i < MaxVehicles; i++)
            {
                _incidentsByPosition[i] = -1;
                _tyreCompoundsByPosition[i] = null;
            }
        }

        private string GetTyreCompound(
            int position,
            int wheelIndex)
        {
            int positionIndex = position - 1;

            if (positionIndex < 0 ||
                positionIndex >= MaxVehicles ||
                wheelIndex < 0 ||
                wheelIndex >= 4 ||
                _tyreCompoundsByPosition[positionIndex] == null)
            {
                return "";
            }

            return _tyreCompoundsByPosition[positionIndex][wheelIndex];
        }

        private static string FormatTyreCompound(
            byte compoundType)
        {
            switch (compoundType)
            {
                case 0:
                    return "Soft";
                case 1:
                    return "Medium";
                case 2:
                    return "Hard";
                case 3:
                    return "Wet";
                default:
                    return "";
            }
        }

        private void UpdateRelativeGaps(int vehicleCount)
        {
            ClearRelativeGaps();

            double trackLength = _scoringInfo.mLapDist;

            if (trackLength <= 0.0 ||
                double.IsNaN(trackLength) ||
                double.IsInfinity(trackLength))
            {
                return;
            }

            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById =
                new Dictionary<int, LMUVehicleTelemetryRelative>();

            for (int telemetryIndex = 0; telemetryIndex < 104; telemetryIndex++)
            {
                LMUVehicleTelemetryRelative telemetry;

                if (!_sharedMemory.TryReadVehicleTelemetryRelative(
                        telemetryIndex,
                        out telemetry))
                {
                    continue;
                }

                if (telemetry.mID >= 0 &&
                    !telemetryById.ContainsKey(telemetry.mID))
                {
                    telemetryById.Add(telemetry.mID, telemetry);
                }
            }

            List<LMUVehicleScoring> vehicles = new List<LMUVehicleScoring>();
            LMUVehicleScoring player = new LMUVehicleScoring();
            bool hasPlayer = false;

            for (int vehicleIndex = 0;
                 vehicleIndex < vehicleCount;
                 vehicleIndex++)
            {
                LMUVehicleScoring vehicle;

                if (!_sharedMemory.TryReadVehicleScoring(
                        vehicleIndex,
                        out vehicle))
                {
                    continue;
                }

                vehicles.Add(vehicle);

                if (vehicle.mIsPlayer)
                {
                    player = vehicle;
                    hasPlayer = true;
                }
            }

            if (!hasPlayer)
                return;

            List<RelativeVehicle> ahead = new List<RelativeVehicle>();
            List<RelativeVehicle> behind = new List<RelativeVehicle>();

            foreach (LMUVehicleScoring vehicle in vehicles)
            {
                if (vehicle.mIsPlayer ||
                    vehicle.mInPits ||
                    vehicle.mInGarageStall ||
                    vehicle.mLapDist < 0.0 ||
                    vehicle.mLapDist >= trackLength)
                {
                    continue;
                }

                double distanceAhead = vehicle.mLapDist - player.mLapDist;

                if (distanceAhead < 0.0)
                    distanceAhead += trackLength;

                double distanceBehind = player.mLapDist - vehicle.mLapDist;

                if (distanceBehind < 0.0)
                    distanceBehind += trackLength;

                if (distanceAhead > 0.5)
                {
                    ahead.Add(new RelativeVehicle
                    {
                        Scoring = vehicle,
                        Distance = distanceAhead
                    });
                }

                if (distanceBehind > 0.5)
                {
                    behind.Add(new RelativeVehicle
                    {
                        Scoring = vehicle,
                        Distance = distanceBehind
                    });
                }
            }

            ahead.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            behind.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            LMUVehicleTelemetryRelative playerTelemetry;

            if (!telemetryById.TryGetValue(player.mID, out playerTelemetry))
                return;

            double cumulativeGap = GetValidRelativeGap(
                playerTelemetry.mTimeGapCarAhead);

            for (int i = 0; i < 3 && i < ahead.Count; i++)
            {
                if (i > 0)
                {
                    LMUVehicleTelemetryRelative previousAheadTelemetry;

                    if (!telemetryById.TryGetValue(
                            ahead[i - 1].Scoring.mID,
                            out previousAheadTelemetry))
                    {
                        break;
                    }

                    double nextGap = GetValidRelativeGap(
                        previousAheadTelemetry.mTimeGapCarAhead);

                    if (double.IsNaN(nextGap))
                        break;

                    cumulativeGap += nextGap;
                }

                if (double.IsNaN(cumulativeGap) ||
                    cumulativeGap > 180.0)
                {
                    break;
                }

                _relativeAhead[i] = FormatRelativeGap(cumulativeGap);
            }

            cumulativeGap = GetValidRelativeGap(
                playerTelemetry.mTimeGapCarBehind);

            for (int i = 0; i < 3 && i < behind.Count; i++)
            {
                if (i > 0)
                {
                    LMUVehicleTelemetryRelative previousBehindTelemetry;

                    if (!telemetryById.TryGetValue(
                            behind[i - 1].Scoring.mID,
                            out previousBehindTelemetry))
                    {
                        break;
                    }

                    double nextGap = GetValidRelativeGap(
                        previousBehindTelemetry.mTimeGapCarBehind);

                    if (double.IsNaN(nextGap))
                        break;

                    cumulativeGap += nextGap;
                }

                if (double.IsNaN(cumulativeGap) ||
                    cumulativeGap > 180.0)
                {
                    break;
                }

                _relativeBehind[i] = FormatRelativeGap(cumulativeGap);
            }
        }

        private void ClearRelativeGaps()
        {
            for (int i = 0; i < 3; i++)
            {
                _relativeAhead[i] = "";
                _relativeBehind[i] = "";
            }
        }

        private static double GetValidRelativeGap(double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                return double.NaN;
            }

            value = Math.Abs(value);

            if (value <= 0.0 ||
                value > 180.0)
            {
                return double.NaN;
            }

            return value;
        }
        private static string FormatRelativeGap(double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value <= 0.0 ||
                value > 180.0)
            {
                return "";
            }

            if (value >= 60.0)
            {
                return TimeSpan.FromSeconds(value).ToString(
                    @"m\:ss",
                    CultureInfo.InvariantCulture
                );
            }

            return value.ToString(
                "F1",
                CultureInfo.InvariantCulture
            );
        }

        private sealed class RelativeVehicle
        {
            public LMUVehicleScoring Scoring;
            public double Distance;
        }

        private static string FormatLapTime(double seconds)
        {
            if (double.IsNaN(seconds) ||
                double.IsInfinity(seconds) ||
                seconds <= 0.0 ||
                seconds > TimeSpan.MaxValue.TotalSeconds)
            {
                return "0";
            }

            return TimeSpan.FromSeconds(seconds).ToString(
                @"m\:ss\.fff",
                CultureInfo.InvariantCulture
            );
        }

        private int GetLapsBehindNext(
            int position)
        {
            if (!_hasScoringInfo ||
                _scoringInfo.mSession < 10 ||
                _scoringInfo.mSession > 13 ||
                position < 1 ||
                position > MaxVehicles ||
                !_hasVehicle[position - 1])
            {
                return 0;
            }

            int laps =
                _vehiclesByPosition[position - 1].mLapsBehindNext;

            return laps > 0 ? laps : 0;
        }

        private int GetLapsBehindClassLeader(
            int position,
            LMUApiVehicle vehicle)
        {
            if (!_hasScoringInfo ||
                _scoringInfo.mSession < 10 ||
                _scoringInfo.mSession > 13 ||
                vehicle == null ||
                vehicle.PositionClass <= 1 ||
                position < 1 ||
                position > MaxVehicles ||
                !_hasVehicle[position - 1])
            {
                return 0;
            }

            LMUApiVehicle classLeader = null;

            for (int overall = 1; overall <= MaxVehicles; overall++)
            {
                LMUApiVehicle candidate =
                    _standingsApi.GetVehicleByPosition(overall);

                if (candidate != null &&
                    candidate.PositionClass == 1 &&
                    string.Equals(
                        candidate.CarClass ?? "",
                        vehicle.CarClass ?? "",
                        StringComparison.OrdinalIgnoreCase))
                {
                    classLeader = candidate;
                    break;
                }
            }

            if (classLeader == null ||
                classLeader.Position < 1 ||
                classLeader.Position > MaxVehicles ||
                !_hasVehicle[classLeader.Position - 1])
            {
                return 0;
            }

            // LMU's laps-behind-leader counter only advances when a car has
            // actually been lapped. Compare the counter against the class
            // leader's counter; mTotalLaps can change at the start/finish line
            // before the leader has truly lapped this car.
            int lapsBehindVehicle =
                _vehiclesByPosition[position - 1].mLapsBehindLeader;
            int lapsBehindClassLeader =
                _vehiclesByPosition[classLeader.Position - 1].mLapsBehindLeader;

            int laps =
                lapsBehindVehicle - lapsBehindClassLeader;

            return laps > 0 ? laps : 0;
        }

        private string FormatGap(
            double value,
            int lapsBehind = 0)
        {
            int session =
                _hasScoringInfo
                    ? _scoringInfo.mSession
                    : 0;

            bool isRace =
                session >= 10 &&
                session <= 13;

            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                (!isRace && value < 0.0))
            {
                return "";
            }

            if (isRace && lapsBehind > 0)
            {
                return lapsBehind.ToString(
                    CultureInfo.InvariantCulture
                ) + "L";
            }

            double absoluteValue = Math.Abs(value);

            if (isRace && absoluteValue >= 60.0)
            {
                return TimeSpan.FromSeconds(absoluteValue).ToString(
                    @"m\:ss",
                    CultureInfo.InvariantCulture
                );
            }

            return absoluteValue.ToString(
                isRace
                    ? "F1"
                    : "F3",
                CultureInfo.InvariantCulture
            );
        }

        private static string FormatVirtualEnergy(
            double veFraction,
            double fuelFraction)
        {
            double value =
                veFraction > 0.0
                    ? veFraction
                    : fuelFraction;

            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0.0)
            {
                return "";
            }

            return (value * 100.0).ToString(
                "F1",
                CultureInfo.InvariantCulture
            );
        }

        private static string DecodeString(
            byte[] bytes)
        {
            if (bytes == null)
                return "";

            int length =
                Array.IndexOf(
                    bytes,
                    (byte)0
                );

            if (length < 0)
                length = bytes.Length;

            return Encoding.UTF8.GetString(
                bytes,
                0,
                length
            ).Trim();
        }

        public void End(
            PluginManager pluginManager)
        {
            _standingsApi.Dispose();
            _sharedMemory.Dispose();
        }
    }

    internal sealed class MMOPluginSettingsControl : UserControl
    {
        private readonly PluginManager _pluginManager;
        private readonly List<string> _propertyNames;
        private readonly ObservableCollection<MMOPropertyDisplayItem> _displayedProperties =
            new ObservableCollection<MMOPropertyDisplayItem>();
        private readonly TextBox _searchBox;
        private readonly ListView _propertyList;
        private readonly TextBlock _resultCount;
        private readonly DispatcherTimer _refreshTimer;

        public MMOPluginSettingsControl(
            PluginManager pluginManager,
            string version,
            List<string> propertyNames)
        {
            _pluginManager = pluginManager;
            _propertyNames = propertyNames ?? new List<string>();
            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 8, 0, 8),
                ToolTip = "Search MMO properties"
            };
            GridView propertyGrid = new GridView();
            propertyGrid.Columns.Add(new GridViewColumn
            {
                Header = "Property",
                Width = 390,
                DisplayMemberBinding = new Binding("Name")
            });
            propertyGrid.Columns.Add(new GridViewColumn
            {
                Header = "Current value",
                Width = 220,
                DisplayMemberBinding = new Binding("Value")
            });
            _propertyList = new ListView
            {
                MinHeight = 420,
                MaxHeight = 700,
                View = propertyGrid,
                ItemsSource = _displayedProperties
            };
            VirtualizingPanel.SetIsVirtualizing(_propertyList, true);
            _resultCount = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 8),
                Opacity = 0.75
            };

            StackPanel content = new StackPanel
            {
                Margin = new Thickness(18)
            };

            content.Children.Add(new TextBlock
            {
                Text = "MMO Plugin",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Version " + (version ?? ""),
                Margin = new Thickness(0, 0, 0, 4)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Author: Kevin_Karas",
                Margin = new Thickness(0, 0, 0, 4)
            });

            System.Windows.Documents.Hyperlink downloadLink =
                new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run(
                        "Download MMO Plugin on OverTake.gg"
                    )
                )
                {
                    NavigateUri = new Uri(
                        "https://www.overtake.gg/downloads/lmu-simhub-mmo-modern-multiclass-overlay-standings-relative-tires.74112/"
                    )
                };
            downloadLink.RequestNavigate += DownloadLink_RequestNavigate;

            TextBlock downloadLine = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 16)
            };
            downloadLine.Inlines.Add(downloadLink);
            content.Children.Add(downloadLine);

            content.Children.Add(new TextBlock
            {
                Text = "Search MMO properties",
                FontWeight = FontWeights.SemiBold
            });

            content.Children.Add(_searchBox);
            content.Children.Add(_resultCount);
            content.Children.Add(_propertyList);

            _searchBox.TextChanged += SearchBox_TextChanged;
            Loaded += SettingsControl_Loaded;
            Unloaded += SettingsControl_Unloaded;

            _refreshTimer = new DispatcherTimer(
                DispatcherPriority.Background
            )
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;

            Content = content;
            ApplyPropertyFilter();
        }

        private void SearchBox_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            ApplyPropertyFilter();
        }

        private void ApplyPropertyFilter()
        {
            string query = (_searchBox.Text ?? "").Trim();
            _displayedProperties.Clear();

            foreach (string name in _propertyNames)
            {
                string fullName = "MMOPlugin." + name;

                if (!string.IsNullOrEmpty(query) &&
                    fullName.IndexOf(
                        query,
                        StringComparison.OrdinalIgnoreCase
                    ) < 0)
                {
                    continue;
                }

                _displayedProperties.Add(
                    new MMOPropertyDisplayItem
                    {
                        Name = fullName,
                        Value = GetCurrentValue(name)
                    }
                );
            }

            _resultCount.Text =
                _displayedProperties.Count.ToString(CultureInfo.InvariantCulture) +
                " properties";
        }

        private void RefreshCurrentValues()
        {
            foreach (MMOPropertyDisplayItem item in _displayedProperties)
            {
                string propertyName = item.Name.Substring(
                    "MMOPlugin.".Length
                );
                item.Value = GetCurrentValue(propertyName);
            }
        }

        private string GetCurrentValue(string propertyName)
        {
            if (_pluginManager == null)
                return "";

            try
            {
                object value = _pluginManager.GetPropertyValue(
                    "MMOPlugin." + propertyName
                );

                if (value == null)
                    value = _pluginManager.GetPropertyValue(propertyName);

                if (value == null)
                    return "";

                IFormattable formattable = value as IFormattable;
                return formattable != null
                    ? formattable.ToString(null, CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }

        private void SettingsControl_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            RefreshCurrentValues();
            _refreshTimer.Start();
        }

        private void SettingsControl_Unloaded(
            object sender,
            RoutedEventArgs e)
        {
            _refreshTimer.Stop();
        }

        private void RefreshTimer_Tick(
            object sender,
            EventArgs e)
        {
            RefreshCurrentValues();
        }

        private static void DownloadLink_RequestNavigate(
            object sender,
            RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }

    internal sealed class MMOPropertyDisplayItem : INotifyPropertyChanged
    {
        private string _value;

        public string Name { get; set; }

        public string Value
        {
            get { return _value; }
            set
            {
                if (_value == value)
                    return;

                _value = value;

                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs("Value"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
