// Copyright (C) 2024-2026 Kevin_Karas
// Portions of the calculations in this plugin are adapted from TinyPedal.
// TinyPedal is Copyright (C) 2022-2026 TinyPedal developers and is licensed
// under GNU GPL version 3 or (at your option) any later version.
// This project is distributed under the same license; see the repository LICENSE.
// Modified for the LMU SimHub plugin by Kevin_Karas (2026).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
    [PluginDescription("Plugin required for Kevins MMO Overlays. Enable left menu for Options!")]
    public class MMOPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string PluginVersion = "0.3.0.0";
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimHub",
            "PluginsData",
            "MMOPlugin",
            "settings.ini"
        );

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

        private readonly int[] _lastVehicleIdByPosition =
            CreateVehicleIdArray();

        private LMUScoringInfo _scoringInfo;

        private bool _hasScoringInfo;
        private volatile bool _showTeamNamesInsteadOfDriverNames;
        private volatile bool _additionalTeamNamesInRelative;
        private volatile int _tyreCompoundSymbolMode;
        private volatile int _lastLaptimeDisplayDurationMs = 10000;
        private volatile bool _showLastPitLapAndTimeInLeaderboard;
        private volatile bool _showLastPitLapAndTimeInRelative;

        private readonly double[] _relativeAhead = new double[3];
        private readonly double[] _relativeBehind = new double[3];
        private readonly int[] _incidentsByPosition = new int[MaxVehicles];
        private readonly Dictionary<int, VehicleTyreState> _tyreStateByVehicleId =
            new Dictionary<int, VehicleTyreState>();
        private readonly Dictionary<int, LastLapTimingState> _lastLapTimingByVehicleId =
            new Dictionary<int, LastLapTimingState>();
        private readonly Dictionary<int, PitStopTimingState> _pitStopTimingByVehicleId =
            new Dictionary<int, PitStopTimingState>();
        private int _playerIncidents = -1;
        private int _pitStopTimingSession = -1;
        private int _lastPlayerVehicleId = -1;
        private double _lastSessionCurrentET = double.NaN;
        private double _lastSessionTimeRemaining = double.NaN;
        private readonly PlayerConsumptionState _playerConsumption =
            new PlayerConsumptionState();

        private const int MaxMappedVehicles = 104;

        private sealed class LastLapTimingState
        {
            public bool HasLapStart;
            public double LapStartET;
            public double LastLapTime;
            public DateTime PopupUntilUtc;
        }

        private sealed class PitStopTimingState
        {
            public bool IsMeasuring;
            public bool WasInPitLane;
            public bool WasInGarage;
            public bool HasStoppedThisPitVisit;
            public bool ResetOnNextStop;
            public bool HasPitEntryLap;
            public double PitStopStartET;
            public double AccumulatedPitTimeSeconds;
            public int PitEntryLap;
            public int PitLap;
            public double LastPitTimeSeconds;
        }

        private sealed class VehicleTyreState
        {
            public bool WasInPits;
            public bool WasInGarage;
            public byte PreviousPitState;
            public bool WasStationaryInPit;
            public bool WasStationaryOnTrack;
            public bool RefreshPending;
            public double RefreshAfterET;
            public DateTime LastRefreshUtc;
            public string[] Compounds = new[] { "", "", "", "" };
        }

        private sealed class PlayerConsumptionState
        {
            public double LastFuelLevel = double.NaN;
            public double LastVeLevel = double.NaN;
            public double FuelUsedThisLap;
            public double VeUsedThisLap;
            public double LastFuelPerLap;
            public double LastVePerLap;
            public double LastValidLapTime;
            public int LastCompletedLaps = -1;
            public int VehicleId = -1;
            public string VehicleIdentity = "";
            public bool LapIncludedPitLane;
            public sbyte LastSector = -1;
            public bool HasSectorOneAnchor;
            public bool HasInitialSectorLapConsumption;
            public bool AwaitingStartFinishBaseline;
            public readonly List<double> FuelLapHistory = new List<double>();
            public readonly List<double> VeLapHistory = new List<double>();
        }

        private static int[] CreateVehicleIdArray()
        {
            int[] ids = new int[MaxVehicles];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = -1;
            return ids;
        }

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            LoadPluginSettings();

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
                name: "MMO_SettingsShowTeamNamesInLeaderboard",
                valueProvider: () => _showTeamNamesInsteadOfDriverNames ? 1 : 0
            );

            this.AttachDelegate(
                name: "MMO_SettingsAdditionalTeamNamesInRelative",
                valueProvider: () => _additionalTeamNamesInRelative ? 1 : 0
            );

            this.AttachDelegate(
                name: "MMO_SettingsTyreCompoundSymbol",
                valueProvider: () => _tyreCompoundSymbolMode
            );

            this.AttachDelegate(
                name: "MMO_SettingsLastLaptimeDisplayDuration",
                valueProvider: () => _lastLaptimeDisplayDurationMs
            );

            this.AttachDelegate(
                name: "MMO_SettingsLeaderboardShowLastPitLapAndTimeDuringRace",
                valueProvider: () => _showLastPitLapAndTimeInLeaderboard ? 1 : 0
            );

            this.AttachDelegate(
                name: "MMO_SettingsRelativeShowLastPitLapAndTimeDuringRace",
                valueProvider: () => _showLastPitLapAndTimeInRelative ? 1 : 0
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

            RegisterPlayerProperties();

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

                        if (vehicle == null)
                            return "";

                        return _showTeamNamesInsteadOfDriverNames
                            ? vehicle.TeamName ?? ""
                            : FormatDriverName(vehicle.DriverName);
                    }
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "DriveNameRelative",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(capturedPosition);

                        if (vehicle == null)
                            return "";

                        string driverName = vehicle.DriverName ?? "";
                        return _additionalTeamNamesInRelative
                            ? FormatRelativeDriverName(driverName, vehicle.TeamName)
                            : driverName;
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
                    valueProvider: () => GetLastLapTime(capturedPosition)
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "LastLaptimePopup",
                    valueProvider: () => GetLastLaptimePopup(capturedPosition)
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
                            return 0.0;

                        int lapsBehindClassLeader =
                            GetLapsBehindClassLeader(
                                capturedPosition,
                                vehicle
                            );

                        return GetGapValue(
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
                            return 0.0;

                        int lapsBehindNext =
                            GetLapsBehindNext(
                                capturedPosition
                            );

                        return GetGapValue(
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

                        return GetPitStatus(capturedPosition, vehicle);
                    }
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "PitLap",
                    valueProvider: () => GetPitStopState(capturedPosition).PitLap
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "PitTime",
                    valueProvider: () => GetPitTimeSeconds(
                        GetPitStopState(capturedPosition).LastPitTimeSeconds
                    )
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
                            return 0.0;

                        return GetVirtualEnergy(vehicle.VeFraction);
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

        private bool TryGetPlayerScoring(
            out int position,
            out LMUVehicleScoring scoringVehicle)
        {
            for (int i = 0; i < MaxVehicles; i++)
            {
                if (_hasVehicle[i] && _vehiclesByPosition[i].mIsPlayer)
                {
                    position = i + 1;
                    scoringVehicle = _vehiclesByPosition[i];
                    return true;
                }
            }

            position = 0;
            scoringVehicle = new LMUVehicleScoring();
            return false;
        }

        private LMUApiVehicle GetPlayerApiVehicle(out int position)
        {
            LMUVehicleScoring ignored;
            if (!TryGetPlayerScoring(out position, out ignored))
                return null;

            return _standingsApi.GetVehicleByPosition(position);
        }

        private string GetPitStatus(int position, LMUApiVehicle apiVehicle)
        {
            int index = position - 1;
            if (_hasScoringInfo && index >= 0 && index < MaxVehicles &&
                _hasVehicle[index])
            {
                // Shared memory belongs to the current simulation frame and
                // avoids showing a stale REST value across session transitions.
                LMUVehicleScoring scoringVehicle = _vehiclesByPosition[index];
                if (scoringVehicle.mInGarageStall)
                    return "InGarage";
                if (scoringVehicle.mInPits)
                {
                    return apiVehicle != null && apiVehicle.Pitting
                        ? "InPit"
                        : "inPitLane";
                }

                return "onTrack";
            }

            // The standings endpoint can temporarily have no row while a new
            // session is loading. Do not report that missing data as on-track.
            if (apiVehicle == null)
                return "unknown";
            if (apiVehicle.InGarageStall)
                return "InGarage";
            if (apiVehicle.Pitting)
                return "InPit";
            if (!string.IsNullOrEmpty(apiVehicle.PitState) &&
                !string.Equals(apiVehicle.PitState, "NONE", StringComparison.OrdinalIgnoreCase))
                return "inPitLane";
            return "onTrack";
        }

        private void InvalidateSharedMemorySnapshot()
        {
            _hasScoringInfo = false;
            _lastPlayerVehicleId = -1;
            for (int i = 0; i < MaxVehicles; i++)
            {
                _hasVehicle[i] = false;
                _vehiclesByPosition[i] = new LMUVehicleScoring();
            }
        }

        private void RegisterPlayerProperties()
        {
            this.AttachDelegate("MMO_PlayerRelativeAhead1", () => _relativeAhead[0]);
            this.AttachDelegate("MMO_PlayerRelativeAhead2", () => _relativeAhead[1]);
            this.AttachDelegate("MMO_PlayerRelativeAhead3", () => _relativeAhead[2]);
            this.AttachDelegate("MMO_PlayerRelativeBehind1", () => _relativeBehind[0]);
            this.AttachDelegate("MMO_PlayerRelativeBehind2", () => _relativeBehind[1]);
            this.AttachDelegate("MMO_PlayerRelativeBehind3", () => _relativeBehind[2]);

            this.AttachDelegate("MMO_PlayerOverall", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null ? vehicle.Position : position;
            });
            this.AttachDelegate("MMO_PlayerClass", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.PositionClass : 0;
            });
            this.AttachDelegate("MMO_PlayerCarNumber", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.CarNumber ?? "" : "";
            });
            this.AttachDelegate("MMO_PlayerCarClass", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.CarClass ?? "" : "";
            });

            string[] wheelNames = { "FL", "FR", "RL", "RR" };
            for (int wheel = 0; wheel < wheelNames.Length; wheel++)
            {
                int capturedWheel = wheel;
                this.AttachDelegate(
                    "MMO_PlayerTyreComp" + wheelNames[wheel],
                    () =>
                    {
                        int position;
                        return TryGetPlayerScoring(out position, out _)
                            ? GetTyreCompound(position, capturedWheel)
                            : "";
                    }
                );
            }

            this.AttachDelegate("MMO_PlayerDriverName", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                if (vehicle == null) return "";
                return _showTeamNamesInsteadOfDriverNames
                    ? vehicle.TeamName ?? ""
                    : FormatDriverName(vehicle.DriverName);
            });
            this.AttachDelegate("MMO_PlayerDriveNameRelative", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                if (vehicle == null) return "";
                string driverName = vehicle.DriverName ?? "";
                return _additionalTeamNamesInRelative
                    ? FormatRelativeDriverName(driverName, vehicle.TeamName)
                    : driverName;
            });
            this.AttachDelegate("MMO_PlayerTeamName", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.TeamName ?? "" : "";
            });
            this.AttachDelegate("MMO_PlayerBestLaptime", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.BestLapTime : 0.0;
            });
            this.AttachDelegate("MMO_PlayerLastLaptime", () =>
            {
                return GetLastLapTimeForPlayer();
            });
            this.AttachDelegate("MMO_PlayerGapToLeader", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null
                    ? GetGapValue(vehicle.GapToLeader, GetLapsBehindClassLeader(position, vehicle))
                    : 0.0;
            });
            this.AttachDelegate("MMO_PlayerIntervalToNext", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null
                    ? GetGapValue(vehicle.IntervalToNext, GetLapsBehindNext(position))
                    : 0.0;
            });
            this.AttachDelegate("MMO_PlayerPitStatus", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return GetPitStatus(position, vehicle);
            });
            this.AttachDelegate("MMO_PlayerPitLap", () =>
                GetPitStopStateByVehicleId(_lastPlayerVehicleId).PitLap
            );
            this.AttachDelegate("MMO_PlayerPitTime", () =>
                GetPitTimeSeconds(
                    GetPitStopStateByVehicleId(_lastPlayerVehicleId).LastPitTimeSeconds
                )
            );
            this.AttachDelegate("MMO_PlayerVirtualEnergy", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null
                    ? GetVirtualEnergy(vehicle.VeFraction)
                    : 0.0;
            });
            this.AttachDelegate("MMO_PlayerVELap", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)_playerConsumption.LastVePerLap
                    : "");
            this.AttachDelegate("MMO_PlayerFuelLap", () => _playerConsumption.LastFuelPerLap);
            this.AttachDelegate("MMO_PlayerVEAvg3Laps", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)AverageLastLaps(_playerConsumption.VeLapHistory)
                    : "");
            this.AttachDelegate("MMO_PlayerVELapsLeft", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)CalculateLapsLeft(
                        GetCurrentVeLevel(),
                        AverageLastLaps(_playerConsumption.VeLapHistory))
                    : "");
            this.AttachDelegate("MMO_PlayerFuelLapsLeft", () =>
                CalculateLapsLeft(
                    GetCurrentFuelLevel(),
                    AverageLastLaps(_playerConsumption.FuelLapHistory)));
            this.AttachDelegate("MMO_PlayerRefuelVE", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)GetVeNeededToFinish()
                    : "");
            this.AttachDelegate("MMO_PlayerPenalties", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.Penalties : 0;
            });
            this.AttachDelegate("MMO_PlayerIncidents", () => _playerIncidents);
            this.AttachDelegate("MMO_PlayerInvalidLap", () =>
            {
                int position;
                LMUVehicleScoring scoring;
                return TryGetPlayerScoring(out position, out scoring) && scoring.mLastLapTime < 0.0
                    ? 1
                    : 0;
            });
            this.AttachDelegate("MMO_PlayerFinishStatus", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null && !string.IsNullOrEmpty(vehicle.FinishStatus) &&
                       !string.Equals(vehicle.FinishStatus, "FSTAT_NONE", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
            });
        }

        public Control GetWPFSettingsControl(
            PluginManager pluginManager)
        {
            return new MMOPluginSettingsControl(
                pluginManager,
                PluginVersion,
                BuildPropertyNames(),
                _showTeamNamesInsteadOfDriverNames,
                SetShowTeamNamesInLeaderboard,
                _additionalTeamNamesInRelative,
                SetAdditionalTeamNamesInRelative,
                _tyreCompoundSymbolMode,
                SetTyreCompoundSymbolMode,
                _showLastPitLapAndTimeInLeaderboard,
                SetShowLastPitLapAndTimeInLeaderboard,
                _showLastPitLapAndTimeInRelative,
                SetShowLastPitLapAndTimeInRelative,
                _lastLaptimeDisplayDurationMs,
                SetLastLaptimeDisplayDuration
            );
        }

        private void LoadPluginSettings()
        {
            _showTeamNamesInsteadOfDriverNames = false;
            _additionalTeamNamesInRelative = false;
            _tyreCompoundSymbolMode = 0;
            _lastLaptimeDisplayDurationMs = 10000;
            _showLastPitLapAndTimeInLeaderboard = false;
            _showLastPitLapAndTimeInRelative = false;
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return;

                string[] lines = File.ReadAllLines(SettingsFilePath);
                bool legacyValue;
                if (lines.Length == 1 && bool.TryParse(lines[0].Trim(), out legacyValue))
                {
                    _showTeamNamesInsteadOfDriverNames = legacyValue;
                    return;
                }

                foreach (string line in lines)
                {
                    int separator = line.IndexOf('=');
                    if (separator < 0)
                        continue;

                    string key = line.Substring(0, separator).Trim();
                    string settingValue = line.Substring(separator + 1).Trim();
                    if (string.Equals(key, "TyreCompoundSymbol", StringComparison.OrdinalIgnoreCase))
                    {
                        int mode;
                        if (int.TryParse(settingValue, out mode) && mode >= 0 && mode <= 2)
                            _tyreCompoundSymbolMode = mode;
                        continue;
                    }

                    if (string.Equals(key, "LastLaptimeDisplayDurationMs", StringComparison.OrdinalIgnoreCase))
                    {
                        int durationMs;
                        if (int.TryParse(settingValue, out durationMs) &&
                            IsValidLastLaptimeDisplayDuration(durationMs))
                            _lastLaptimeDisplayDurationMs = durationMs;
                        continue;
                    }

                    if (string.Equals(key, "ShowLastPitLapAndTimeDuringRace", StringComparison.OrdinalIgnoreCase))
                    {
                        bool showPitData;
                        if (bool.TryParse(settingValue, out showPitData))
                        {
                            _showLastPitLapAndTimeInLeaderboard = showPitData;
                            _showLastPitLapAndTimeInRelative = showPitData;
                        }
                        continue;
                    }

                    if (string.Equals(key, "LeaderboardShowLastPitLapAndTimeDuringRace", StringComparison.OrdinalIgnoreCase))
                    {
                        bool showPitData;
                        if (bool.TryParse(settingValue, out showPitData))
                            _showLastPitLapAndTimeInLeaderboard = showPitData;
                        continue;
                    }

                    if (string.Equals(key, "RelativeShowLastPitLapAndTimeDuringRace", StringComparison.OrdinalIgnoreCase))
                    {
                        bool showPitData;
                        if (bool.TryParse(settingValue, out showPitData))
                            _showLastPitLapAndTimeInRelative = showPitData;
                        continue;
                    }

                    bool value;
                    if (!bool.TryParse(settingValue, out value))
                        continue;

                    if (string.Equals(key, "ShowTeamNamesInLeaderboard", StringComparison.OrdinalIgnoreCase))
                        _showTeamNamesInsteadOfDriverNames = value;
                    else if (string.Equals(key, "AdditionalTeamNamesInRelative", StringComparison.OrdinalIgnoreCase))
                        _additionalTeamNamesInRelative = value;
                }
            }
            catch
            {
                _showTeamNamesInsteadOfDriverNames = false;
                _additionalTeamNamesInRelative = false;
                _tyreCompoundSymbolMode = 0;
                _lastLaptimeDisplayDurationMs = 10000;
                _showLastPitLapAndTimeInLeaderboard = false;
                _showLastPitLapAndTimeInRelative = false;
            }
        }

        private void SetShowTeamNamesInLeaderboard(bool value)
        {
            _showTeamNamesInsteadOfDriverNames = value;
            SavePluginSettings();
        }

        private void SetAdditionalTeamNamesInRelative(bool value)
        {
            _additionalTeamNamesInRelative = value;
            SavePluginSettings();
        }

        private void SetTyreCompoundSymbolMode(int value)
        {
            _tyreCompoundSymbolMode = value >= 0 && value <= 2 ? value : 0;
            SavePluginSettings();
        }

        private void SetLastLaptimeDisplayDuration(int value)
        {
            _lastLaptimeDisplayDurationMs =
                IsValidLastLaptimeDisplayDuration(value) ? value : 10000;
            SavePluginSettings();
        }

        private static bool IsValidLastLaptimeDisplayDuration(int value)
        {
            return value == 5000 || value == 10000 ||
                value == 30000 || value == 45000;
        }

        private void SetShowLastPitLapAndTimeInLeaderboard(bool value)
        {
            _showLastPitLapAndTimeInLeaderboard = value;
            SavePluginSettings();
        }

        private void SetShowLastPitLapAndTimeInRelative(bool value)
        {
            _showLastPitLapAndTimeInRelative = value;
            SavePluginSettings();
        }

        private void SavePluginSettings()
        {
            try
            {
                string directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllLines(SettingsFilePath, new[]
                {
                    "ShowTeamNamesInLeaderboard=" + _showTeamNamesInsteadOfDriverNames,
                    "AdditionalTeamNamesInRelative=" + _additionalTeamNamesInRelative,
                    "TyreCompoundSymbol=" + _tyreCompoundSymbolMode,
                    "LastLaptimeDisplayDurationMs=" + _lastLaptimeDisplayDurationMs,
                    "LeaderboardShowLastPitLapAndTimeDuringRace=" + _showLastPitLapAndTimeInLeaderboard,
                    "RelativeShowLastPitLapAndTimeDuringRace=" + _showLastPitLapAndTimeInRelative
                });
            }
            catch
            {
                // Keep the option active for this session when settings cannot be saved.
            }
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
                "MMO_SettingsShowTeamNamesInLeaderboard",
                "MMO_SettingsAdditionalTeamNamesInRelative",
                "MMO_SettingsTyreCompoundSymbol",
                "MMO_SettingsLastLaptimeDisplayDuration",
                "MMO_SettingsLeaderboardShowLastPitLapAndTimeDuringRace",
                "MMO_SettingsRelativeShowLastPitLapAndTimeDuringRace",
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

            string[] playerSuffixes =
            {
                "Overall", "Class", "CarNumber", "CarClass",
                "TyreCompFL", "TyreCompFR", "TyreCompRL", "TyreCompRR",
                "DriverName", "DriveNameRelative", "TeamName",
                "BestLaptime", "LastLaptime", "GapToLeader",
                "IntervalToNext", "PitStatus", "PitLap", "PitTime", "VirtualEnergy",
                "VELap", "FuelLap", "VEAvg3Laps", "VELapsLeft", "FuelLapsLeft", "RefuelVE",
                "Penalties", "Incidents", "InvalidLap", "FinishStatus"
            };

            foreach (string suffix in playerSuffixes)
                names.Add("MMO_Player" + suffix);

            for (int i = 1; i <= 3; i++)
            {
                names.Add("MMO_PlayerRelativeAhead" + i);
                names.Add("MMO_PlayerRelativeBehind" + i);
            }

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
                "DriveNameRelative",
                "TeamName",
                "BestLaptime",
                "LastLaptime",
                "LastLaptimePopup",
                "GapToLeader",
                "IntervalToNext",
                "PitStatus",
                "PitLap",
                "PitTime",
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
                InvalidateSharedMemorySnapshot();
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
                InvalidateSharedMemorySnapshot();
                ClearRelativeGaps();
                ClearTrackLimitPoints();
                return;
            }

            bool sessionTypeChanged =
                _pitStopTimingSession != _scoringInfo.mSession;
            bool sessionRestarted =
                !sessionTypeChanged && HasSessionRestarted(_scoringInfo);
            bool resetSessionValues = sessionTypeChanged || sessionRestarted;

            if (resetSessionValues)
            {
                ResetSessionCaches();
                _pitStopTimingSession = _scoringInfo.mSession;
            }

            if (!double.IsNaN(_scoringInfo.mCurrentET) &&
                !double.IsInfinity(_scoringInfo.mCurrentET))
            {
                _lastSessionCurrentET = _scoringInfo.mCurrentET;
            }
            if (!float.IsNaN(_scoringInfo.mSessionTimeRemaining) &&
                !float.IsInfinity(_scoringInfo.mSessionTimeRemaining))
            {
                _lastSessionTimeRemaining = _scoringInfo.mSessionTimeRemaining;
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

                _lastVehicleIdByPosition[positionIndex] = vehicle.mID;

                if (vehicle.mIsPlayer)
                    _lastPlayerVehicleId = vehicle.mID;
            }

            if (resetSessionValues)
            {
                // Treat vehicles already in the pitlane/garage at session start as
                // baseline state, not as a new pit entry or a completed stop.
                for (int i = 0; i < MaxVehicles; i++)
                {
                    if (!_hasVehicle[i])
                        continue;

                    LMUVehicleScoring vehicle = _vehiclesByPosition[i];
                    _pitStopTimingByVehicleId[vehicle.mID] =
                        new PitStopTimingState
                        {
                            WasInPitLane = vehicle.mInPits,
                            WasInGarage = vehicle.mInGarageStall
                        };
                }
            }

            UpdateRelativeGaps(vehicleCount);
            UpdateTrackLimitPoints();
            UpdateLastLapTimes();
            UpdatePitStopTimes();
            UpdatePlayerConsumption();
        }

        private bool HasSessionRestarted(LMUScoringInfo scoringInfo)
        {
            bool elapsedTimeReset =
                !double.IsNaN(_lastSessionCurrentET) &&
                !double.IsInfinity(_lastSessionCurrentET) &&
                !double.IsNaN(scoringInfo.mCurrentET) &&
                !double.IsInfinity(scoringInfo.mCurrentET) &&
                _lastSessionCurrentET > 5.0 &&
                scoringInfo.mCurrentET < _lastSessionCurrentET - 5.0;

            bool remainingTimeReset =
                !double.IsNaN(_lastSessionTimeRemaining) &&
                !double.IsInfinity(_lastSessionTimeRemaining) &&
                !float.IsNaN(scoringInfo.mSessionTimeRemaining) &&
                !float.IsInfinity(scoringInfo.mSessionTimeRemaining) &&
                _lastSessionTimeRemaining > 0.0 &&
                scoringInfo.mSessionTimeRemaining > _lastSessionTimeRemaining + 5.0;

            return elapsedTimeReset || remainingTimeReset;
        }

        private void ResetSessionCaches()
        {
            _pitStopTimingByVehicleId.Clear();
            _lastLapTimingByVehicleId.Clear();
            _tyreStateByVehicleId.Clear();
            ResetPlayerConsumptionForSession();
            _lastPlayerVehicleId = -1;
            for (int i = 0; i < MaxVehicles; i++)
                _lastVehicleIdByPosition[i] = -1;
        }

        private void ResetPlayerConsumption()
        {
            _playerConsumption.LastFuelLevel = double.NaN;
            _playerConsumption.LastVeLevel = double.NaN;
            _playerConsumption.FuelUsedThisLap = 0.0;
            _playerConsumption.VeUsedThisLap = 0.0;
            _playerConsumption.LastFuelPerLap = 0.0;
            _playerConsumption.LastVePerLap = 0.0;
            _playerConsumption.LastValidLapTime = 0.0;
            _playerConsumption.LastCompletedLaps = -1;
            _playerConsumption.VehicleId = -1;
            _playerConsumption.VehicleIdentity = "";
            _playerConsumption.LapIncludedPitLane = false;
            _playerConsumption.LastSector = -1;
            _playerConsumption.HasSectorOneAnchor = false;
            _playerConsumption.HasInitialSectorLapConsumption = false;
            _playerConsumption.AwaitingStartFinishBaseline = false;
            _playerConsumption.FuelLapHistory.Clear();
            _playerConsumption.VeLapHistory.Clear();
        }

        private void ResetPlayerConsumptionForSession()
        {
            // Keep the last lap rates and their history so the new session can
            // use them immediately when the player remains in the same car.
            // The vehicle ID is intentionally retained; UpdatePlayerConsumption
            // performs a full reset if a different car is selected.
            _playerConsumption.LastFuelLevel = double.NaN;
            _playerConsumption.LastVeLevel = double.NaN;
            _playerConsumption.FuelUsedThisLap = 0.0;
            _playerConsumption.VeUsedThisLap = 0.0;
            _playerConsumption.LastValidLapTime = 0.0;
            _playerConsumption.LastCompletedLaps = -1;
            _playerConsumption.LapIncludedPitLane = false;
            _playerConsumption.LastSector = -1;
            _playerConsumption.HasSectorOneAnchor = false;
            _playerConsumption.HasInitialSectorLapConsumption = false;
            _playerConsumption.AwaitingStartFinishBaseline = false;
        }

        private void UpdatePlayerConsumption()
        {
            int position;
            LMUVehicleScoring scoring;
            if (!TryGetPlayerScoring(out position, out scoring))
                return;

            LMUApiVehicle apiVehicle =
                _standingsApi.GetVehicleByPosition(position);
            if (apiVehicle == null)
            {
                _playerConsumption.LastFuelLevel = double.NaN;
                _playerConsumption.LastVeLevel = double.NaN;
                return;
            }

            string vehicleIdentity = GetConsumptionVehicleIdentity(scoring, apiVehicle);
            if (_playerConsumption.VehicleId != scoring.mID)
            {
                bool sameCar =
                    !string.IsNullOrEmpty(_playerConsumption.VehicleIdentity) &&
                    string.Equals(
                        _playerConsumption.VehicleIdentity,
                        vehicleIdentity,
                        StringComparison.OrdinalIgnoreCase
                    );

                if (sameCar)
                    ResetPlayerConsumptionForSession();
                else
                    ResetPlayerConsumption();

                _playerConsumption.VehicleId = scoring.mID;
            }
            if (!string.IsNullOrEmpty(vehicleIdentity))
                _playerConsumption.VehicleIdentity = vehicleIdentity;

            double fuelLevel;
            double fuelCapacity;
            bool hasFuel = _standingsApi.TryGetFuelInfo(
                out fuelLevel,
                out fuelCapacity
            ) && IsFinite(fuelLevel) && fuelLevel >= 0.0 && fuelCapacity > 0.0;
            if (!hasFuel)
                _playerConsumption.LastFuelLevel = double.NaN;
            double veLevel = apiVehicle.VeFraction * 100.0;
            bool hasVe = IsFinite(veLevel) && veLevel > 0.0;
            if (!hasVe)
            {
                _playerConsumption.LastVeLevel = double.NaN;
                _playerConsumption.VeUsedThisLap = 0.0;
            }

            int completedLaps = scoring.mTotalLaps;
            if (_playerConsumption.LastCompletedLaps >= 0 &&
                completedLaps < _playerConsumption.LastCompletedLaps)
            {
                // A reset from the garage can restart the car's lap counter
                // without changing the session. Keep completed-lap rates, but
                // establish fresh telemetry baselines for this run.
                ResetPlayerConsumptionForSession();
            }

            bool completedLap =
                _playerConsumption.LastCompletedLaps >= 0 &&
                completedLaps > _playerConsumption.LastCompletedLaps;
            if (completedLap)
            {
                double lastLapTime = scoring.mLastLapTime;
                if (IsValidLapTime(lastLapTime))
                    _playerConsumption.LastValidLapTime = lastLapTime;

                if (_playerConsumption.AwaitingStartFinishBaseline)
                {
                    // The initial sector-to-line outlap measurement is only
                    // a baseline. Start collecting full laps from this line.
                    _playerConsumption.AwaitingStartFinishBaseline = false;
                    ResetConsumptionWindow(hasFuel, fuelLevel, hasVe, veLevel);
                }
                else if (_playerConsumption.HasInitialSectorLapConsumption)
                {
                    bool lapHadPitLane =
                        _playerConsumption.LapIncludedPitLane ||
                        scoring.mInPits || scoring.mInGarageStall;
                    if (!lapHadPitLane)
                    {
                        AddLapConsumption(
                            _playerConsumption.FuelLapHistory,
                            _playerConsumption.FuelUsedThisLap,
                            ref _playerConsumption.LastFuelPerLap
                        );
                        if (hasVe)
                            AddLapConsumption(
                                _playerConsumption.VeLapHistory,
                                _playerConsumption.VeUsedThisLap,
                                ref _playerConsumption.LastVePerLap
                            );
                    }
                    ResetConsumptionWindow(hasFuel, fuelLevel, hasVe, veLevel);
                }
            }

            // The sector-1 anchor lets an outlap produce an early first
            // consumption measurement at the same point one lap later.
            bool sectorOneEnded =
                (_playerConsumption.LastSector == 1 && scoring.mSector == 2) ||
                (_playerConsumption.LastSector < 0 && scoring.mSector == 2);
            if (sectorOneEnded)
            {
                if (!_playerConsumption.HasSectorOneAnchor)
                {
                    ResetConsumptionWindow(hasFuel, fuelLevel, hasVe, veLevel);
                    _playerConsumption.HasSectorOneAnchor = true;
                }
                else if (!_playerConsumption.HasInitialSectorLapConsumption)
                {
                    bool lapHadPitLane =
                        _playerConsumption.LapIncludedPitLane ||
                        scoring.mInPits || scoring.mInGarageStall;
                    if (!lapHadPitLane)
                    {
                        AddLapConsumption(
                            _playerConsumption.FuelLapHistory,
                            _playerConsumption.FuelUsedThisLap,
                            ref _playerConsumption.LastFuelPerLap
                        );
                        if (hasVe)
                            AddLapConsumption(
                                _playerConsumption.VeLapHistory,
                                _playerConsumption.VeUsedThisLap,
                                ref _playerConsumption.LastVePerLap
                            );
                    }
                    ResetConsumptionWindow(hasFuel, fuelLevel, hasVe, veLevel);
                    _playerConsumption.HasInitialSectorLapConsumption = true;
                    _playerConsumption.AwaitingStartFinishBaseline = true;
                }
            }

            _playerConsumption.LastCompletedLaps = completedLaps;
            _playerConsumption.LastSector = scoring.mSector;
            _playerConsumption.LapIncludedPitLane |=
                scoring.mInPits || scoring.mInGarageStall;

            double speed = Math.Sqrt(
                scoring.mLocalVel.x * scoring.mLocalVel.x +
                scoring.mLocalVel.y * scoring.mLocalVel.y +
                scoring.mLocalVel.z * scoring.mLocalVel.z
            );

            if (hasFuel)
            {
                if (IsFinite(_playerConsumption.LastFuelLevel))
                {
                    double fuelDelta =
                        _playerConsumption.LastFuelLevel - fuelLevel;
                    if (fuelDelta > 0.0 || (fuelDelta < 0.0 && speed > 1.0))
                        _playerConsumption.FuelUsedThisLap += fuelDelta;
                }
                _playerConsumption.LastFuelLevel = fuelLevel;
            }

            if (hasVe)
            {
                if (IsFinite(_playerConsumption.LastVeLevel))
                {
                    double veDelta = _playerConsumption.LastVeLevel - veLevel;
                    if (veDelta > 0.0 || (veDelta < 0.0 && speed > 1.0))
                        _playerConsumption.VeUsedThisLap += veDelta;
                }
                _playerConsumption.LastVeLevel = veLevel;
            }
        }

        private void ResetConsumptionWindow(
            bool hasFuel,
            double fuelLevel,
            bool hasVe,
            double veLevel)
        {
            _playerConsumption.FuelUsedThisLap = 0.0;
            _playerConsumption.VeUsedThisLap = 0.0;
            _playerConsumption.LapIncludedPitLane = false;
            _playerConsumption.LastFuelLevel = hasFuel ? fuelLevel : double.NaN;
            _playerConsumption.LastVeLevel = hasVe ? veLevel : double.NaN;
        }

        private static string GetConsumptionVehicleIdentity(
            LMUVehicleScoring scoring,
            LMUApiVehicle apiVehicle)
        {
            string vehicleFile = DecodeString(scoring.mVehFilename);
            string vehicleName = DecodeString(scoring.mVehicleName);
            string carClass = apiVehicle != null ? apiVehicle.CarClass ?? "" : "";
            string carNumber = apiVehicle != null ? apiVehicle.CarNumber ?? "" : "";

            string identity = string.Join(
                "|",
                vehicleFile.Trim(),
                vehicleName.Trim(),
                carClass.Trim(),
                carNumber.Trim()
            );
            return identity.Replace("|", "").Length == 0
                ? ""
                : identity;
        }

        private static void AddLapConsumption(
            List<double> history,
            double consumption,
            ref double latest)
        {
            if (!IsFinite(consumption) || consumption <= 0.0)
            {
                // Keep the previous valid lap value when this lap produced no
                // usable consumption sample.
                return;
            }

            latest = consumption;
            history.Add(consumption);
            if (history.Count > 3)
                history.RemoveAt(0);
        }

        private static double AverageLastLaps(List<double> history)
        {
            if (history == null || history.Count == 0)
                return 0.0;

            double total = 0.0;
            for (int i = 0; i < history.Count; i++)
                total += history[i];
            return total / history.Count;
        }

        private double GetCurrentFuelLevel()
        {
            double level;
            double capacity;
            return _standingsApi.TryGetFuelInfo(out level, out capacity) &&
                IsFinite(level) && level >= 0.0 && capacity > 0.0
                    ? level
                    : 0.0;
        }

        private double GetCurrentVeLevel()
        {
            LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
            double level = vehicle != null ? vehicle.VeFraction * 100.0 : 0.0;
            return IsFinite(level) && level > 0.0 ? level : 0.0;
        }

        private static double CalculateLapsLeft(double currentLevel, double averagePerLap)
        {
            return IsFinite(currentLevel) && IsFinite(averagePerLap) && averagePerLap > 0.0
                ? currentLevel / averagePerLap
                : 0.0;
        }

        private object GetVeNeededToFinish()
        {
            double currentVe = GetCurrentVeLevel();
            if (currentVe <= 0.0)
                return "";

            double averageVePerLap =
                AverageLastLaps(_playerConsumption.VeLapHistory);
            if (averageVePerLap <= 0.0)
                return 0.0;

            LMUVehicleScoring scoring;
            if (!TryGetPlayerScoring(out _, out scoring))
                return "";

            double lapsRemaining;
            if (!TryGetRaceLapsRemaining(scoring, out lapsRemaining))
                return "";

            return lapsRemaining * averageVePerLap - currentVe;
        }

        private bool TryGetRaceLapsRemaining(
            LMUVehicleScoring player,
            out double lapsRemaining)
        {
            lapsRemaining = 0.0;

            // In practice and qualifying, estimate remaining lap-equivalents
            // from this session's clock and the player's pace. Do not use the
            // race leader's finish-line timing for these sessions.
            if (_scoringInfo.mSession >= 1 && _scoringInfo.mSession <= 8)
            {
                LMUApiVehicle pqPlayerApi = GetPlayerApiVehicle(out _);
                double pqPlayerPace = GetLapPace(player, pqPlayerApi, true);
                double pqTimeRemaining = _scoringInfo.mSessionTimeRemaining;
                if (!IsValidLapTime(pqPlayerPace) || !IsFinite(pqTimeRemaining))
                    return false;

                lapsRemaining = Math.Max(0.0, pqTimeRemaining) / pqPlayerPace;
                return true;
            }

            double trackLength = _scoringInfo.mLapDist;
            double playerProgress;
            if (!TryGetLapProgress(player, trackLength, out playerProgress))
                return false;

            bool isTimedRace =
                _scoringInfo.mSession >= 10 &&
                _scoringInfo.mSession <= 13 &&
                IsFinite(_scoringInfo.mEndET) &&
                _scoringInfo.mEndET > 0.0;

            if (_scoringInfo.mMaxLaps > 0 && !isTimedRace)
            {
                lapsRemaining = Math.Max(
                    0.0,
                    _scoringInfo.mMaxLaps - player.mTotalLaps - playerProgress
                );
                return true;
            }

            // In a timed race, the overall leader's first line crossing after the
            // clock expires starts/ends the final-race finish sequence. Cars that
            // crossed before that leader must complete another lap before their
            // own finish-line crossing counts.
            LMUVehicleScoring leader;
            if (!TryGetLeaderScoring(out leader))
                return false;

            LMUApiVehicle playerApi = GetPlayerApiVehicle(out _);
            LMUApiVehicle leaderApi = _standingsApi.GetVehicleByPosition(1);
            double playerPace = GetLapPace(player, playerApi, true);
            double leaderPace = GetLapPace(leader, leaderApi, false);
            if (!IsValidLapTime(playerPace) || !IsValidLapTime(leaderPace))
                return false;

            double timeRemaining = _scoringInfo.mSessionTimeRemaining;
            if (!IsFinite(timeRemaining))
                return false;
            timeRemaining = Math.Max(0.0, timeRemaining);

            double playerTimeToNextLine;
            double leaderTimeToNextLine;
            if (!TryGetTimeToNextLine(
                    player,
                    playerProgress,
                    playerPace,
                    out playerTimeToNextLine) ||
                !TryGetTimeToNextLine(
                    leader,
                    GetLapProgress(leader, trackLength),
                    leaderPace,
                    out leaderTimeToNextLine))
            {
                return false;
            }

            bool leaderHasTakenCheckered =
                HasFinishStatus(leaderApi, "FINISHED");
            double leaderFinishAfterNow;
            if (leaderHasTakenCheckered)
            {
                leaderFinishAfterNow = 0.0;
            }
            else
            {
                // Find the leader's first predicted line crossing at or after
                // the timer reaches zero. Keep this as elapsed time from now;
                // subtracting timeRemaining here would leave only the small
                // post-zero remainder and make RefuelVE think the race is over.
                double crossingsAfterNext = Math.Ceiling(
                    Math.Max(0.0, timeRemaining - leaderTimeToNextLine) /
                    leaderPace - 1e-9
                );
                leaderFinishAfterNow = leaderTimeToNextLine +
                    crossingsAfterNext * leaderPace;
            }

            bool playerAlreadyFinished = HasFinishStatus(playerApi, "FINISHED") ||
                HasFinishStatus(playerApi, "DNF");
            if (playerAlreadyFinished)
            {
                lapsRemaining = 0.0;
                return true;
            }

            // If the player's next line crossing occurs before the leader's
            // checkered crossing, continue to the first later crossing.
            double crossingsAfterPlayerNext = playerTimeToNextLine >= leaderFinishAfterNow
                ? 0.0
                : Math.Ceiling(
                    (leaderFinishAfterNow - playerTimeToNextLine) / playerPace - 1e-9
                );
            lapsRemaining = Math.Max(
                0.0,
                1.0 - playerProgress + crossingsAfterPlayerNext
            );
            return true;
        }

        private bool TryGetLeaderScoring(out LMUVehicleScoring leader)
        {
            for (int i = 0; i < MaxVehicles; i++)
            {
                if (_hasVehicle[i] && _vehiclesByPosition[i].mPlace == 1)
                {
                    leader = _vehiclesByPosition[i];
                    return true;
                }
            }

            leader = new LMUVehicleScoring();
            return false;
        }

        private double GetLapPace(
            LMUVehicleScoring vehicle,
            LMUApiVehicle apiVehicle,
            bool isPlayer)
        {
            if (IsValidLapTime(vehicle.mEstimatedLapTime))
                return vehicle.mEstimatedLapTime;
            if (IsValidLapTime(vehicle.mLastLapTime))
                return vehicle.mLastLapTime;
            if (apiVehicle != null && IsValidLapTime(apiVehicle.LastLapTime))
                return apiVehicle.LastLapTime;
            if (isPlayer && IsValidLapTime(_playerConsumption.LastValidLapTime))
                return _playerConsumption.LastValidLapTime;
            if (apiVehicle != null && IsValidLapTime(apiVehicle.BestLapTime))
                return apiVehicle.BestLapTime;
            return 0.0;
        }

        private static bool TryGetTimeToNextLine(
            LMUVehicleScoring vehicle,
            double lapProgress,
            double lapPace,
            out double timeToLine)
        {
            timeToLine = 0.0;
            if (!IsValidLapTime(lapPace) || !IsFinite(lapProgress))
                return false;

            double elapsed = vehicle.mTimeIntoLap;
            if (IsFinite(elapsed) && elapsed >= 0.0 && elapsed <= lapPace)
            {
                timeToLine = Math.Max(0.0, lapPace - elapsed);
                return true;
            }

            timeToLine = Math.Max(0.0, (1.0 - lapProgress) * lapPace);
            return IsFinite(timeToLine);
        }

        private static bool TryGetLapProgress(
            LMUVehicleScoring vehicle,
            double trackLength,
            out double progress)
        {
            progress = 0.0;
            if (!IsFinite(trackLength) || trackLength <= 0.0)
                return false;

            progress = GetLapProgress(vehicle, trackLength);
            return IsFinite(progress);
        }

        private static double GetLapProgress(
            LMUVehicleScoring vehicle,
            double trackLength)
        {
            if (!IsFinite(trackLength) || trackLength <= 0.0 ||
                !IsFinite(vehicle.mLapDist))
            {
                return 0.0;
            }

            return Math.Max(0.0, Math.Min(1.0, vehicle.mLapDist / trackLength));
        }

        private static bool HasFinishStatus(
            LMUApiVehicle vehicle,
            string expectedStatus)
        {
            return vehicle != null &&
                !string.IsNullOrEmpty(vehicle.FinishStatus) &&
                vehicle.FinishStatus.IndexOf(
                    expectedStatus,
                    StringComparison.OrdinalIgnoreCase
                ) >= 0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private void UpdatePitStopTimes()
        {
            if (_scoringInfo.mSession < 10 || _scoringInfo.mSession > 13)
                return;

            double elapsedTime = _scoringInfo.mCurrentET;
            if (double.IsNaN(elapsedTime) || double.IsInfinity(elapsedTime))
                return;

            for (int i = 0; i < MaxVehicles; i++)
            {
                if (!_hasVehicle[i])
                    continue;

                LMUVehicleScoring vehicle = _vehiclesByPosition[i];
                if (vehicle.mID < 0)
                    continue;

                PitStopTimingState state;
                if (!_pitStopTimingByVehicleId.TryGetValue(vehicle.mID, out state))
                {
                    state = new PitStopTimingState();
                    _pitStopTimingByVehicleId.Add(vehicle.mID, state);
                }

                bool inPitLane = vehicle.mInPits;
                if (inPitLane && !state.WasInPitLane)
                {
                    // Capture the lap at pit-lane entry. It is committed to PitLap
                    // only if this visit becomes an actual stop.
                    state.PitEntryLap = Math.Max(1, vehicle.mTotalLaps + 1);
                    state.HasPitEntryLap = true;
                }

                // A garage visit ends the current pit-stop measurement. Keep PitLap
                // until the car leaves the garage and returns to the track.
                if (vehicle.mInGarageStall)
                {
                    state.IsMeasuring = false;
                    state.WasInGarage = true;
                    state.HasStoppedThisPitVisit = false;
                    state.ResetOnNextStop = false;
                    state.AccumulatedPitTimeSeconds = 0.0;
                    state.LastPitTimeSeconds = 0.0;
                    state.WasInPitLane = inPitLane;
                    continue;
                }

                if (state.WasInGarage && !inPitLane)
                {
                    state.PitLap = 0;
                    state.LastPitTimeSeconds = 0.0;
                    state.AccumulatedPitTimeSeconds = 0.0;
                    state.HasPitEntryLap = false;
                    state.HasStoppedThisPitVisit = false;
                    state.ResetOnNextStop = false;
                }
                state.WasInGarage = false;

                double speed = Math.Sqrt(
                    vehicle.mLocalVel.x * vehicle.mLocalVel.x +
                    vehicle.mLocalVel.y * vehicle.mLocalVel.y +
                    vehicle.mLocalVel.z * vehicle.mLocalVel.z
                );
                bool stationaryInPit = inPitLane &&
                    !double.IsNaN(speed) && !double.IsInfinity(speed) && speed < 0.1;

                if (stationaryInPit && !state.IsMeasuring)
                {
                    // Only start a new measurement after an actual stop. Merely
                    // entering and leaving the lane (for example a drive-through)
                    // leaves the previous result intact.
                    if (state.ResetOnNextStop)
                    {
                        state.AccumulatedPitTimeSeconds = 0.0;
                        state.LastPitTimeSeconds = 0.0;
                        state.ResetOnNextStop = false;
                    }

                    state.IsMeasuring = true;
                    state.HasStoppedThisPitVisit = true;
                    state.PitStopStartET = elapsedTime;
                    if (state.HasPitEntryLap)
                    {
                        state.PitLap = state.PitEntryLap;
                        state.HasPitEntryLap = false;
                    }
                }
                else if (state.IsMeasuring && !stationaryInPit)
                {
                    AccumulatePitStopSegment(state, elapsedTime);
                    state.IsMeasuring = false;
                }

                if (state.WasInPitLane && !inPitLane &&
                    state.HasStoppedThisPitVisit)
                    state.ResetOnNextStop = true;

                state.WasInPitLane = inPitLane;
            }
        }

        private static void AccumulatePitStopSegment(
            PitStopTimingState state,
            double elapsedTime)
        {
            double duration = elapsedTime - state.PitStopStartET;
            if (duration < 0.0 || double.IsNaN(duration) || double.IsInfinity(duration))
                return;

            state.AccumulatedPitTimeSeconds += duration;
            state.LastPitTimeSeconds = state.AccumulatedPitTimeSeconds;
        }

        private PitStopTimingState GetPitStopState(int position)
        {
            return GetPitStopStateByVehicleId(GetVehicleIdAtPosition(position));
        }

        private PitStopTimingState GetPitStopStateByVehicleId(int vehicleId)
        {
            PitStopTimingState state;
            if (vehicleId < 0 ||
                !_pitStopTimingByVehicleId.TryGetValue(vehicleId, out state))
                return new PitStopTimingState();

            if (state.IsMeasuring && _hasScoringInfo)
            {
                double elapsedTime = _scoringInfo.mCurrentET;
                double currentSegment = elapsedTime - state.PitStopStartET;
                if (currentSegment >= 0.0 && !double.IsNaN(currentSegment) &&
                    !double.IsInfinity(currentSegment))
                {
                    state.LastPitTimeSeconds =
                        state.AccumulatedPitTimeSeconds + currentSegment;
                }
            }

            return state;
        }

        private static string GetPitTimeSeconds(double seconds)
        {
            if (!IsFinite(seconds) || seconds < 0.0)
                seconds = 0.0;

            int totalSeconds = (int)Math.Floor(seconds);
            if (totalSeconds < 60)
            {
                return totalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
            }

            int minutes = totalSeconds / 60;
            int remainingSeconds = totalSeconds % 60;
            return minutes.ToString(CultureInfo.InvariantCulture) + ":" +
                remainingSeconds.ToString("00", CultureInfo.InvariantCulture);
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

                // Briefly missing timestamps must not erase the cached lap time.
                if (currentLapStartET <= 0.0)
                    continue;

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

                        int position = positionIndex + 1;
                        LMUApiVehicle apiVehicle =
                            _standingsApi.GetVehicleByPosition(position);
                        if (string.Equals(
                                GetPitStatus(position, apiVehicle),
                                "onTrack",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            state.PopupUntilUtc = DateTime.UtcNow.AddMilliseconds(
                                _lastLaptimeDisplayDurationMs
                            );
                        }
                        else
                        {
                            state.PopupUntilUtc = DateTime.MinValue;
                        }
                    }

                    state.LapStartET = currentLapStartET;
                }
                else if (currentLapStartET < state.LapStartET - 0.001)
                {
                    // Session reset or a vehicle slot/lap timer reset.
                    state.LapStartET = currentLapStartET;
                    state.HasLapStart = true;
                }
            }
        }

        private static bool IsValidLapTime(double lapTime)
        {
            return lapTime > 0.0 &&
                !double.IsNaN(lapTime) &&
                !double.IsInfinity(lapTime);
        }

        private double GetLastLapTime(int position)
        {
            int vehicleId = GetVehicleIdAtPosition(position);
            return GetCachedLastLapTime(vehicleId);
        }

        private int GetLastLaptimePopup(int position)
        {
            int vehicleId = GetVehicleIdAtPosition(position);
            LastLapTimingState state;
            if (vehicleId < 0 ||
                !_lastLapTimingByVehicleId.TryGetValue(vehicleId, out state) ||
                !IsValidLapTime(state.LastLapTime) ||
                DateTime.UtcNow >= state.PopupUntilUtc)
            {
                return 0;
            }

            LMUApiVehicle apiVehicle = _standingsApi.GetVehicleByPosition(position);
            return string.Equals(
                GetPitStatus(position, apiVehicle),
                "onTrack",
                StringComparison.OrdinalIgnoreCase
            ) ? 1 : 0;
        }

        private double GetLastLapTimeForPlayer()
        {
            int vehicleId = _lastPlayerVehicleId;
            if (vehicleId < 0)
            {
                int position;
                LMUVehicleScoring ignored;
                if (TryGetPlayerScoring(out position, out ignored))
                    vehicleId = ignored.mID;
            }

            return GetCachedLastLapTime(vehicleId);
        }

        private double GetCachedLastLapTime(int vehicleId)
        {
            LastLapTimingState state;
            return vehicleId >= 0 &&
                _lastLapTimingByVehicleId.TryGetValue(vehicleId, out state) &&
                IsValidLapTime(state.LastLapTime)
                    ? state.LastLapTime
                    : 0.0;
        }

        private int GetVehicleIdAtPosition(int position)
        {
            int index = position - 1;
            if (index < 0 || index >= MaxVehicles)
                return -1;

            if (_hasVehicle[index])
                return _vehiclesByPosition[index].mID;

            return _lastVehicleIdByPosition[index];
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
                double speed = Math.Sqrt(
                    vehicle.mLocalVel.x * vehicle.mLocalVel.x +
                    vehicle.mLocalVel.y * vehicle.mLocalVel.y +
                    vehicle.mLocalVel.z * vehicle.mLocalVel.z
                );
                bool stationaryInPit = vehicle.mInPits &&
                    !double.IsNaN(speed) && !double.IsInfinity(speed) &&
                    speed < 0.1;
                bool onTrack = !vehicle.mInPits && !vehicle.mInGarageStall;
                bool stationaryOnTrack = onTrack &&
                    !double.IsNaN(speed) && !double.IsInfinity(speed) &&
                    speed < 0.1;

                VehicleTyreState tyreState;
                if (!_tyreStateByVehicleId.TryGetValue(vehicle.mID, out tyreState))
                {
                    tyreState = new VehicleTyreState
                    {
                        WasInPits = vehicle.mInPits,
                        WasInGarage = vehicle.mInGarageStall,
                        PreviousPitState = vehicle.mPitState,
                        WasStationaryInPit = stationaryInPit,
                        WasStationaryOnTrack = stationaryOnTrack,
                        RefreshPending = true,
                        RefreshAfterET = _scoringInfo.mCurrentET
                    };
                    _tyreStateByVehicleId.Add(vehicle.mID, tyreState);
                }
                else
                {
                    bool exitedGarage =
                        tyreState.WasInGarage && !vehicle.mInGarageStall;
                    bool leftPitLane =
                        (tyreState.WasInPits && !vehicle.mInPits) ||
                        (tyreState.PreviousPitState != 0 && vehicle.mPitState == 0);
                    bool startedMoving = onTrack &&
                        tyreState.WasStationaryOnTrack && speed >= 0.1;
                    bool stationaryOnTrackRefreshDue = stationaryOnTrack &&
                        (DateTime.UtcNow - tyreState.LastRefreshUtc).TotalSeconds >= 5.0;

                    if (exitedGarage || leftPitLane || startedMoving ||
                        stationaryOnTrackRefreshDue)
                    {
                        tyreState.RefreshPending = true;
                        // Garage telemetry can still contain the old tyre set
                        // on the transition frame; allow it to roll out first.
                        tyreState.RefreshAfterET = _scoringInfo.mCurrentET +
                            (exitedGarage ? 0.5 : 0.0);
                    }

                    tyreState.WasInPits = vehicle.mInPits;
                    tyreState.WasInGarage = vehicle.mInGarageStall;
                    tyreState.PreviousPitState = vehicle.mPitState;
                    tyreState.WasStationaryInPit = stationaryInPit;
                    tyreState.WasStationaryOnTrack = stationaryOnTrack;
                }

                LMUVehicleTelemetryRelative telemetry;

                if (!telemetryById.TryGetValue(vehicle.mID, out telemetry))
                    continue;

                // LMU exposes normalized track-limit steps in this telemetry byte.
                // Keep the raw value for in-game comparison; scale can be confirmed empirically.
                _incidentsByPosition[positionIndex] = telemetry.mTrackLimitsSteps;

                if (tyreState.RefreshPending &&
                    _scoringInfo.mCurrentET >= tyreState.RefreshAfterET)
                {
                    tyreState.Compounds = new[]
                    {
                        FormatTyreCompound(telemetry.mTyreCompFL),
                        FormatTyreCompound(telemetry.mTyreCompFR),
                        FormatTyreCompound(telemetry.mTyreCompRL),
                        FormatTyreCompound(telemetry.mTyreCompRR)
                    };
                    tyreState.RefreshPending = false;
                    tyreState.LastRefreshUtc = DateTime.UtcNow;
                }

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
            }
        }

        private string GetTyreCompound(
            int position,
            int wheelIndex)
        {
            if (wheelIndex < 0 || wheelIndex >= 4)
                return "";

            int vehicleId = GetVehicleIdAtPosition(position);
            VehicleTyreState state;
            return vehicleId >= 0 &&
                _tyreStateByVehicleId.TryGetValue(vehicleId, out state) &&
                state.Compounds != null
                    ? state.Compounds[wheelIndex]
                    : "";
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

                _relativeAhead[i] = cumulativeGap;
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

                _relativeBehind[i] = cumulativeGap;
            }
        }

        private void ClearRelativeGaps()
        {
            for (int i = 0; i < 3; i++)
            {
                _relativeAhead[i] = 0.0;
                _relativeBehind[i] = 0.0;
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
        private sealed class RelativeVehicle
        {
            public LMUVehicleScoring Scoring;
            public double Distance;
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

        private object GetGapValue(
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
                return 0.0;
            }

            if (isRace && lapsBehind > 0)
            {
                return lapsBehind.ToString(CultureInfo.InvariantCulture) + "L";
            }

            double absoluteValue = Math.Abs(value);
            if (!isRace)
                return absoluteValue;

            // Race gaps are displayed directly as seconds, then as m:ss once
            // they reach one minute. Overlay formatting is not required.
            if (absoluteValue >= 60.0)
            {
                int totalSeconds = (int)Math.Floor(absoluteValue);
                return (totalSeconds / 60).ToString(CultureInfo.InvariantCulture) + ":" +
                    (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture);
            }

            return absoluteValue.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static double GetVirtualEnergy(double veFraction)
        {
            if (double.IsNaN(veFraction) ||
                double.IsInfinity(veFraction) ||
                veFraction <= 0.0)
            {
                return 0.0;
            }

            return veFraction * 100.0;
        }

        private static string FormatRelativeDriverName(
            string driverName,
            string teamName)
        {
            string normalizedDriverName = (driverName ?? "").Trim();
            if (string.IsNullOrEmpty(normalizedDriverName))
                return "";

            string[] nameParts = normalizedDriverName.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries
            );

            string formattedDriverName = nameParts.Length > 1
                ? nameParts[0].Substring(0, 1).ToUpperInvariant() + ". " +
                  string.Join(" ", nameParts, 1, nameParts.Length - 1).ToUpperInvariant()
                : normalizedDriverName.ToUpperInvariant();

            string normalizedTeamName = (teamName ?? "").Trim();
            return string.IsNullOrEmpty(normalizedTeamName)
                ? formattedDriverName
                : formattedDriverName + " (" + normalizedTeamName.ToUpperInvariant() + ")";
        }

        private static string FormatDriverName(string driverName)
        {
            string normalizedDriverName = (driverName ?? "").Trim();
            if (string.IsNullOrEmpty(normalizedDriverName))
                return "";

            string[] nameParts = normalizedDriverName.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries
            );

            if (nameParts.Length < 2)
                return normalizedDriverName;

            return nameParts[0].Substring(0, 1).ToUpperInvariant() + ". " +
                   string.Join(" ", nameParts, 1, nameParts.Length - 1);
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
        private const string LatestReleaseApi = "https://api.github.com/repos/St54Kevin/MMOPlugin/releases/latest";
        private readonly PluginManager _pluginManager;
        private readonly List<string> _propertyNames;
        private readonly ObservableCollection<MMOPropertyDisplayItem> _displayedProperties =
            new ObservableCollection<MMOPropertyDisplayItem>();
        private readonly TextBox _searchBox;
        private readonly ListView _propertyList;
        private readonly TextBlock _resultCount;
        private readonly DispatcherTimer _refreshTimer;
        private readonly TextBlock _updateStatus;
        private readonly Button _checkUpdateButton;
        private readonly Button _installUpdateButton;
        private Border _pluginUpdateBadge;
        private Button _checkOverlaysButton;
        private Button _installAllOverlaysButton;
        private Button _updateAllOverlaysButton;
        private TextBlock _overlayStatus;
        private ListView _overlayList;
        private TabItem _overlaysTab;
        private Border _overlayUpdateBadge;
        private readonly List<OverlayPackageItem> _overlayItems =
            new List<OverlayPackageItem>();
        private string _overlayPackageVersion;
        private readonly CheckBox _leaderboardTeamNamesOption;
        private readonly CheckBox _additionalRelativeTeamNamesOption;
        private readonly CheckBox _leaderboardPitDataOption;
        private readonly CheckBox _relativePitDataOption;
        private readonly ComboBox _tyreCompoundSymbolOption;
        private readonly ComboBox _lastLaptimeDisplayDurationOption;
        private readonly Action<bool> _setLeaderboardTeamNamesOption;
        private readonly Action<bool> _setAdditionalRelativeTeamNamesOption;
        private readonly Action<int> _setTyreCompoundSymbolMode;
        private readonly Action<int> _setLastLaptimeDisplayDuration;
        private readonly Action<bool> _setShowLastPitLapAndTimeInLeaderboard;
        private readonly Action<bool> _setShowLastPitLapAndTimeInRelative;
        private GitHubRelease _latestRelease;
        private bool _checkingUpdate;
        private bool _installingUpdate;
        private bool _pluginUpdateStatusKnown;
        private bool _pluginUpdateAvailable;
        private bool _checkingOverlays;
        private bool _installingOverlays;

        private bool PluginIsCurrent
        {
            get { return _pluginUpdateStatusKnown && !_pluginUpdateAvailable; }
        }

        private sealed class OverlayPackageItem
        {
            public string FileName;
            public string DisplayName;
            public string InstalledDisplayName;
            public string OverlayKey;
            public string PackageVersion;
            public string InstalledVersion;
            public string PackageDirectoryName;
            public string StagedPath;
            public string InstalledPath;
            public string InstalledExpandedDirectory;
            public string Hash;
            public string StatusText;
            public bool IsInstalled;
            public bool NeedsInstall;
            public bool NeedsUpdate;
        }

        private sealed class OverlayIdentity
        {
            public string DisplayName;
            public string Key;
            public string Version;
        }

        private sealed class InstalledOverlay
        {
            public string Path;
            public string ExpandedDirectory;
            public string DisplayName;
            public string Version;
            public string Hash;
        }

        public MMOPluginSettingsControl(
            PluginManager pluginManager,
            string version,
            List<string> propertyNames,
            bool showTeamNamesInsteadOfDriverNames,
            Action<bool> setLeaderboardTeamNamesOption,
            bool additionalTeamNamesInRelative,
            Action<bool> setAdditionalRelativeTeamNamesOption,
            int tyreCompoundSymbolMode,
            Action<int> setTyreCompoundSymbolMode,
            bool showLastPitLapAndTimeInLeaderboard,
            Action<bool> setShowLastPitLapAndTimeInLeaderboard,
            bool showLastPitLapAndTimeInRelative,
            Action<bool> setShowLastPitLapAndTimeInRelative,
            int lastLaptimeDisplayDurationMs,
            Action<int> setLastLaptimeDisplayDuration)
        {
            _pluginManager = pluginManager;
            _propertyNames = propertyNames ?? new List<string>();
            _setLeaderboardTeamNamesOption = setLeaderboardTeamNamesOption;
            _setAdditionalRelativeTeamNamesOption = setAdditionalRelativeTeamNamesOption;
            _setTyreCompoundSymbolMode = setTyreCompoundSymbolMode;
            _setLastLaptimeDisplayDuration = setLastLaptimeDisplayDuration;
            _setShowLastPitLapAndTimeInLeaderboard = setShowLastPitLapAndTimeInLeaderboard;
            _setShowLastPitLapAndTimeInRelative = setShowLastPitLapAndTimeInRelative;
            Background = Brushes.Transparent;
            Foreground = Brushes.White;
            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8, 5, 8, 5),
                ToolTip = "Search MMO properties",
                MinWidth = 220,
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White
            };
            GridView propertyGrid = new GridView();
            propertyGrid.Columns.Add(new GridViewColumn
            {
                Header = "Property",
                Width = 380,
                DisplayMemberBinding = new Binding("Name")
            });
            propertyGrid.Columns.Add(new GridViewColumn
            {
                Header = "Current value",
                Width = 190,
                DisplayMemberBinding = new Binding("Value")
            });
            _propertyList = new ListView
            {
                View = propertyGrid,
                ItemsSource = _displayedProperties,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                FontSize = 12,
                Foreground = Brushes.White
            };
            VirtualizingPanel.SetIsVirtualizing(_propertyList, true);
            _resultCount = new TextBlock
            {
                Margin = new Thickness(0),
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };

            _updateStatus = new TextBlock
            {
                Text = "Checking GitHub releases…",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 12,
                Foreground = Brushes.White
            };
            _checkUpdateButton = new Button
            {
                Content = "Check",
                Padding = new Thickness(10, 5, 10, 5),
                MinWidth = 72,
                Margin = new Thickness(0, 0, 7, 0)
            };
            _installUpdateButton = new Button
            {
                Content = "Update",
                Padding = new Thickness(10, 5, 10, 5),
                MinWidth = 76,
                Visibility = Visibility.Collapsed,
                IsEnabled = false
            };
            _leaderboardTeamNamesOption = new CheckBox
            {
                Content = "Show Team Names in Leaderboard",
                IsChecked = showTeamNamesInsteadOfDriverNames,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Top
            };
            _additionalRelativeTeamNamesOption = new CheckBox
            {
                Content = "Additional Team Names in Relative",
                IsChecked = additionalTeamNamesInRelative,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6),
                VerticalAlignment = VerticalAlignment.Top
            };
            _tyreCompoundSymbolOption = new ComboBox
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _tyreCompoundSymbolOption.Items.Add("Display for all cars");
            _tyreCompoundSymbolOption.Items.Add("Only own class");
            _tyreCompoundSymbolOption.Items.Add("Own class, only when different");
            _tyreCompoundSymbolOption.SelectedIndex =
                tyreCompoundSymbolMode >= 0 && tyreCompoundSymbolMode <= 2
                    ? tyreCompoundSymbolMode
                    : 0;
            _lastLaptimeDisplayDurationOption = new ComboBox
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _lastLaptimeDisplayDurationOption.Items.Add("5s");
            _lastLaptimeDisplayDurationOption.Items.Add("10s");
            _lastLaptimeDisplayDurationOption.Items.Add("30s");
            _lastLaptimeDisplayDurationOption.Items.Add("45s");
            int[] durationValues = { 5000, 10000, 30000, 45000 };
            _lastLaptimeDisplayDurationOption.SelectedIndex =
                Array.IndexOf(durationValues, lastLaptimeDisplayDurationMs);
            if (_lastLaptimeDisplayDurationOption.SelectedIndex < 0)
                _lastLaptimeDisplayDurationOption.SelectedIndex = 1;
            _leaderboardPitDataOption = new CheckBox
            {
                Content = "Show last pit lap and time during race",
                IsChecked = showLastPitLapAndTimeInLeaderboard,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6)
            };
            _relativePitDataOption = new CheckBox
            {
                Content = "Show last pit lap and time during race",
                IsChecked = showLastPitLapAndTimeInRelative,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6)
            };

            Grid root = new Grid
            {
                Margin = new Thickness(12)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border header = new Border
            {
                Padding = new Thickness(0, 0, 0, 10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 75, 83)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            System.Windows.Documents.Hyperlink downloadLink =
                new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("Overlay")
                )
                {
                    NavigateUri = new Uri(
                        "https://www.overtake.gg/downloads/lmu-simhub-mmo-modern-multiclass-overlay-standings-relative-tires.74112/"
                    )
                };
            downloadLink.RequestNavigate += DownloadLink_RequestNavigate;

            System.Windows.Documents.Hyperlink releasesLink =
                new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("GitHub")
                )
                {
                    NavigateUri = new Uri("https://github.com/St54Kevin/MMOPlugin/releases")
                };
            releasesLink.RequestNavigate += DownloadLink_RequestNavigate;

            DockPanel headerLayout = new DockPanel { LastChildFill = true };
            TextBlock currentVersion = new TextBlock
            {
                Text = "v" + (version ?? ""),
                Opacity = 0.72,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            TextBlock headerLinks = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            headerLinks.Inlines.Add(downloadLink);
            headerLinks.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            headerLinks.Inlines.Add(releasesLink);
            StackPanel headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerActions.Children.Add(headerLinks);
            headerActions.Children.Add(currentVersion);
            DockPanel.SetDock(headerActions, Dock.Right);
            headerLayout.Children.Add(headerActions);
            StackPanel titleGroup = new StackPanel { Orientation = Orientation.Horizontal };
            Image pluginLogo = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri(
                        "pack://application:,,,/MMOPlugin;component/rsz_logo_transp.png",
                        UriKind.Absolute
                    )
                ),
                Width = 38,
                Height = 38,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "MMO Plugin by Kevin_Karas"
            };
            titleGroup.Children.Add(pluginLogo);
            titleGroup.Children.Add(new TextBlock
            {
                Text = "MMO Plugin",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            titleGroup.Children.Add(new TextBlock
            {
                Text = "by Kevin_Karas",
                Opacity = 0.65,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            headerLayout.Children.Add(titleGroup);
            header.Child = headerLayout;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Border updateCard = new Border
            {
                Margin = new Thickness(0, 9, 0, 10),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 75, 83)),
                Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128))
            };
            DockPanel updateLayout = new DockPanel { LastChildFill = true };
            StackPanel updateButtons = new StackPanel { Orientation = Orientation.Horizontal };
            updateButtons.Children.Add(_checkUpdateButton);
            updateButtons.Children.Add(_installUpdateButton);
            DockPanel.SetDock(updateButtons, Dock.Right);
            updateLayout.Children.Add(updateButtons);
            updateLayout.Children.Add(_updateStatus);
            updateCard.Child = updateLayout;
            Grid.SetRow(updateCard, 1);
            root.Children.Add(updateCard);

            StackPanel propertyHeading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 3)
            };
            propertyHeading.Children.Add(new TextBlock
            {
                Text = "Properties",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            propertyHeading.Children.Add(_resultCount);
            propertyHeading.Children.Add(new TextBlock
            {
                Text = "· 2s refresh interval",
                Opacity = 0.6,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            Grid propertyArea = new Grid();
            propertyArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            propertyArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            propertyArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(propertyHeading, 0);
            Grid.SetRow(_searchBox, 1);
            Grid.SetRow(_propertyList, 2);
            propertyArea.Children.Add(propertyHeading);
            propertyArea.Children.Add(_searchBox);
            propertyArea.Children.Add(_propertyList);
            Grid.SetRow(propertyArea, 2);
            Grid.SetRowSpan(propertyArea, 2);
            root.Children.Add(propertyArea);

            _searchBox.TextChanged += SearchBox_TextChanged;
            _checkUpdateButton.Click += CheckUpdateButton_Click;
            _installUpdateButton.Click += InstallUpdateButton_Click;
            _leaderboardTeamNamesOption.Checked += TeamNameOption_Changed;
            _leaderboardTeamNamesOption.Unchecked += TeamNameOption_Changed;
            _additionalRelativeTeamNamesOption.Checked += TeamNameOption_Changed;
            _additionalRelativeTeamNamesOption.Unchecked += TeamNameOption_Changed;
            _leaderboardPitDataOption.Checked += PitDataOption_Changed;
            _leaderboardPitDataOption.Unchecked += PitDataOption_Changed;
            _relativePitDataOption.Checked += PitDataOption_Changed;
            _relativePitDataOption.Unchecked += PitDataOption_Changed;
            _tyreCompoundSymbolOption.SelectionChanged += TyreCompoundSymbolOption_SelectionChanged;
            _lastLaptimeDisplayDurationOption.SelectionChanged += LastLaptimeDisplayDurationOption_SelectionChanged;
            Loaded += SettingsControl_Loaded;
            Unloaded += SettingsControl_Unloaded;

            _refreshTimer = new DispatcherTimer(
                DispatcherPriority.Background
            )
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;

            StackPanel leaderboardOptions = new StackPanel();
            leaderboardOptions.Children.Add(_leaderboardTeamNamesOption);
            leaderboardOptions.Children.Add(new TextBlock
            {
                Text = "Replaces the DriverName properties with the corresponding TeamName.",
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(22, 0, 0, 0)
            });
            leaderboardOptions.Children.Add(_leaderboardPitDataOption);

            StackPanel relativeOptions = new StackPanel();
            relativeOptions.Children.Add(_additionalRelativeTeamNamesOption);
            relativeOptions.Children.Add(new TextBlock
            {
                Text = "Adds the team name to DriveNameRelative, formatted as initial, surname, and team.",
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(22, 0, 0, 0)
            });
            relativeOptions.Children.Add(_relativePitDataOption);

            StackPanel generalOptions = new StackPanel();
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Tyre Compound Symbol:",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            generalOptions.Children.Add(_tyreCompoundSymbolOption);
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Last Laptime display duration",
                FontSize = 13,
                Margin = new Thickness(0, 16, 0, 6)
            });
            generalOptions.Children.Add(_lastLaptimeDisplayDurationOption);
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Affects duration of last laptime popup on leaderboard and relative",
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(0, 4, 0, 0)
            });

            StackPanel settingsPage = new StackPanel
            {
                Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            settingsPage.Children.Add(new GroupBox
            {
                Header = "Leaderboard",
                Content = leaderboardOptions,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10)
            });
            settingsPage.Children.Add(new GroupBox
            {
                Header = "Relative",
                Content = relativeOptions,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10)
            });
            settingsPage.Children.Add(new GroupBox
            {
                Header = "General",
                Content = generalOptions,
                Padding = new Thickness(10)
            });

            Style tabItemStyle = new Style(typeof(TabItem));
            tabItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            tabItemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            tabItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 7, 18, 6)));
            tabItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Gray));
            tabItemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 20.0));
            ControlTemplate tabTemplate = new ControlTemplate(typeof(TabItem));
            FrameworkElementFactory tabRoot = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory headerBorder = new FrameworkElementFactory(typeof(Border));
            headerBorder.Name = "HeaderBorder";
            headerBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            headerBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 5, 0));
            headerBorder.SetValue(Border.PaddingProperty, new Thickness(13, 7, 13, 9));
            FrameworkElementFactory headerContent = new FrameworkElementFactory(typeof(ContentPresenter));
            headerContent.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            headerContent.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            headerContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            headerBorder.AppendChild(headerContent);
            tabRoot.AppendChild(headerBorder);

            FrameworkElementFactory selectionIndicator = new FrameworkElementFactory(typeof(Border));
            selectionIndicator.Name = "SelectionIndicator";
            selectionIndicator.SetValue(Border.HeightProperty, 3.0);
            selectionIndicator.SetValue(Border.BackgroundProperty, Brushes.LimeGreen);
            selectionIndicator.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
            selectionIndicator.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            tabRoot.AppendChild(selectionIndicator);
            tabTemplate.VisualTree = tabRoot;
            tabTemplate.Triggers.Add(new Trigger
            {
                Property = TabItem.IsSelectedProperty,
                Value = true,
                Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SelectionIndicator") }
            });
            tabItemStyle.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate));
            tabItemStyle.Triggers.Add(new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
                Setters = { new Setter(Control.ForegroundProperty, Brushes.White) }
            });
            tabItemStyle.Triggers.Add(new Trigger
            {
                Property = TabItem.IsSelectedProperty,
                Value = true,
                Setters = { new Setter(Control.ForegroundProperty, Brushes.LimeGreen) }
            });

            TabControl tabs = new TabControl { ItemContainerStyle = tabItemStyle };
            tabs.Items.Add(new TabItem
            {
                Header = CreateUpdateTabHeader("Plugin", out _pluginUpdateBadge),
                Content = root
            });
            BuildOverlaysPage(tabs);
            tabs.Items.Add(new TabItem
            {
                Header = CreateTabHeader("Settings"),
                Content = settingsPage
            });
            tabs.SelectionChanged += PluginTabs_SelectionChanged;

            Content = tabs;
            ApplyPropertyFilter();
        }

        private void BuildOverlaysPage(TabControl tabs)
        {
            _overlayStatus = new TextBlock
            {
                Text = "Check GitHub for the latest overlay package.",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 12,
                Foreground = Brushes.White
            };
            _checkOverlaysButton = new Button
            {
                Content = "Check",
                Padding = new Thickness(10, 5, 10, 5),
                MinWidth = 72,
                Margin = new Thickness(0, 0, 7, 0)
            };
            _installAllOverlaysButton = new Button
            {
                Content = "Install All",
                Padding = new Thickness(10, 5, 10, 5),
                MinWidth = 84,
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = false
            };
            _updateAllOverlaysButton = new Button
            {
                Content = "Update All",
                Padding = new Thickness(10, 5, 10, 5),
                MinWidth = 84,
                IsEnabled = false
            };

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            actions.Children.Add(_checkOverlaysButton);
            actions.Children.Add(_installAllOverlaysButton);
            actions.Children.Add(_updateAllOverlaysButton);

            DockPanel statusPanel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(actions, Dock.Right);
            statusPanel.Children.Add(actions);
            statusPanel.Children.Add(_overlayStatus);

            _overlayList = new ListView
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                FontSize = 12,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 10, 0, 0)
            };
            VirtualizingPanel.SetIsVirtualizing(_overlayList, true);

            Grid page = new Grid
            {
                Margin = new Thickness(12)
            };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(statusPanel, 0);
            Grid.SetRow(_overlayList, 1);
            page.Children.Add(statusPanel);
            page.Children.Add(_overlayList);

            _checkOverlaysButton.Click += CheckOverlaysButton_Click;
            _installAllOverlaysButton.Click += InstallAllOverlaysButton_Click;
            _updateAllOverlaysButton.Click += UpdateAllOverlaysButton_Click;

            _overlaysTab = new TabItem
            {
                Header = CreateUpdateTabHeader("Overlays", out _overlayUpdateBadge),
                Content = page
            };
            tabs.Items.Add(_overlaysTab);
        }

        private static UIElement CreateUpdateTabHeader(string title, out Border updateBadge)
        {
            StackPanel header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            TextBlock badgeText = new TextBlock
            {
                Text = "U",
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            updateBadge = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Orange,
                Child = badgeText,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(5, -6, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Update available"
            };
            header.Children.Add(updateBadge);
            return header;
        }

        private static UIElement CreateTabHeader(string title)
        {
            Border unusedBadge;
            return CreateUpdateTabHeader(title, out unusedBadge);
        }

        private static void SetUpdateBadge(Border badge, bool visible)
        {
            if (badge != null)
                badge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void PluginTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TabControl tabs = sender as TabControl;
            if (tabs != null && ReferenceEquals(e.OriginalSource, tabs) &&
                ReferenceEquals(tabs.SelectedItem, _overlaysTab))
                await CheckOverlaysAsync();
        }

        private void RefreshOverlayList()
        {
            if (_overlayList == null)
                return;

            _overlayList.Items.Clear();
            foreach (OverlayPackageItem item in _overlayItems)
            {
                Grid row = new Grid
                {
                    Margin = new Thickness(5, 3, 5, 3),
                    MinHeight = 30
                };
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(2, GridUnitType.Star)
                });
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(3, GridUnitType.Star)
                });
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(90)
                });

                TextBlock name = new TextBlock
                {
                    Text = item.IsInstalled
                        ? item.InstalledDisplayName
                        : GetOverlayNameWithoutVersion(item.DisplayName) + " · Not installed",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.FileName
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                TextBlock status = new TextBlock
                {
                    Text = item.StatusText,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.StatusText,
                    Foreground = item.NeedsUpdate
                        ? Brushes.Orange
                        : item.IsInstalled
                            ? Brushes.LightGreen
                            : Brushes.OrangeRed
                };
                Grid.SetColumn(status, 1);
                row.Children.Add(status);

                if (item.NeedsInstall || item.NeedsUpdate)
                {
                    Button action = new Button
                    {
                        Content = item.NeedsInstall ? "Install" : "Update",
                        Tag = item,
                        Padding = new Thickness(8, 3, 8, 3),
                        MinWidth = 72,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        IsEnabled = PluginIsCurrent,
                        ToolTip = PluginIsCurrent
                            ? null
                            : "Update MMO Plugin before installing or updating overlays."
                    };
                    action.Click += OverlayRowAction_Click;
                    Grid.SetColumn(action, 2);
                    row.Children.Add(action);
                }

                _overlayList.Items.Add(row);
            }

            _installAllOverlaysButton.IsEnabled = _overlayItems.Count > 0 && PluginIsCurrent;
            _updateAllOverlaysButton.IsEnabled =
                PluginIsCurrent && _overlayItems.Exists(item => item.NeedsUpdate);
            _installAllOverlaysButton.ToolTip = PluginIsCurrent
                ? null
                : "Update MMO Plugin before installing or updating overlays.";
            _updateAllOverlaysButton.ToolTip = PluginIsCurrent
                ? null
                : "Update MMO Plugin before installing or updating overlays.";
            SetUpdateBadge(_overlayUpdateBadge, _overlayItems.Exists(item => item.NeedsUpdate));
        }

        private void TeamNameOption_Changed(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, _leaderboardTeamNamesOption))
            {
                if (_setLeaderboardTeamNamesOption != null)
                    _setLeaderboardTeamNamesOption(_leaderboardTeamNamesOption.IsChecked == true);
            }
            else if (ReferenceEquals(sender, _additionalRelativeTeamNamesOption))
            {
                if (_setAdditionalRelativeTeamNamesOption != null)
                    _setAdditionalRelativeTeamNamesOption(_additionalRelativeTeamNamesOption.IsChecked == true);
            }
        }

        private void TyreCompoundSymbolOption_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_setTyreCompoundSymbolMode != null &&
                _tyreCompoundSymbolOption.SelectedIndex >= 0)
                _setTyreCompoundSymbolMode(_tyreCompoundSymbolOption.SelectedIndex);
        }

        private void LastLaptimeDisplayDurationOption_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_setLastLaptimeDisplayDuration == null ||
                _lastLaptimeDisplayDurationOption.SelectedIndex < 0)
                return;

            int[] durationValues = { 5000, 10000, 30000, 45000 };
            _setLastLaptimeDisplayDuration(
                durationValues[_lastLaptimeDisplayDurationOption.SelectedIndex]
            );
        }

        private void PitDataOption_Changed(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, _leaderboardPitDataOption))
            {
                if (_setShowLastPitLapAndTimeInLeaderboard != null)
                    _setShowLastPitLapAndTimeInLeaderboard(
                        _leaderboardPitDataOption.IsChecked == true
                    );
            }
            else if (ReferenceEquals(sender, _relativePitDataOption))
            {
                if (_setShowLastPitLapAndTimeInRelative != null)
                    _setShowLastPitLapAndTimeInRelative(
                        _relativePitDataOption.IsChecked == true
                    );
            }
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
            if (!_checkingUpdate && _latestRelease == null)
                CheckForUpdatesAsync();
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

        private async void CheckOverlaysButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckOverlaysAsync();
        }

        private async Task CheckOverlaysAsync()
        {
            if (_checkingOverlays || _installingOverlays)
                return;

            _checkingOverlays = true;
            _checkOverlaysButton.IsEnabled = false;
            _installAllOverlaysButton.IsEnabled = false;
            _updateAllOverlaysButton.IsEnabled = false;
            _overlayStatus.Text = "Checking the latest GitHub release and downloading overlays…";
            _overlayStatus.Foreground = Brushes.White;

            try
            {
                GitHubRelease release = await Task.Run(LoadLatestRelease);
                GitHubReleaseAsset asset = FindOverlayAsset(release);
                if (asset == null)
                {
                    _overlayItems.Clear();
                    RefreshOverlayList();
                    _overlayStatus.Text = "The latest release does not contain an MMOverlays_*.zip asset.";
                    return;
                }

                Version githubVersion;
                if (!Version.TryParse(ExtractOverlayVersion(asset.Name), out githubVersion))
                    throw new InvalidDataException(
                        "The overlay ZIP name does not contain a usable version number: " + asset.Name
                    );

                string cacheRoot = GetOverlayCacheDirectory();
                Directory.CreateDirectory(cacheRoot);
                string archivePath = Path.Combine(cacheRoot, Path.GetFileName(asset.Name));
                _overlayStatus.Text = "Downloading the latest overlay package v" +
                    githubVersion + " from GitHub…";

                List<OverlayPackageItem> items = await Task.Run(
                    () => DownloadAndInspectOverlayPackage(asset, archivePath)
                );
                _overlayItems.Clear();
                _overlayItems.AddRange(items);
                _overlayPackageVersion = ExtractOverlayVersion(asset.Name) ?? "latest";
                RefreshOverlayList();
                _overlayStatus.Text = "Downloaded latest overlay package v" +
                    _overlayPackageVersion + ". " +
                    items.Count.ToString(CultureInfo.InvariantCulture) +
                    " overlay(s) compared with installed versions.";
            }
            catch (Exception ex)
            {
                _overlayItems.Clear();
                RefreshOverlayList();
                _overlayStatus.Text = "Could not check overlays: " + ex.Message;
                _overlayStatus.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                _checkingOverlays = false;
                _checkOverlaysButton.IsEnabled = true;
            }
        }

        private static GitHubReleaseAsset FindOverlayAsset(GitHubRelease release)
        {
            if (release == null || release.Assets == null)
                return null;

            GitHubReleaseAsset latestAsset = null;
            Version latestVersion = null;
            foreach (GitHubReleaseAsset asset in release.Assets)
            {
                string name = asset.Name ?? "";
                Version candidateVersion;
                if (!name.StartsWith("MMOverlays_", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    !Version.TryParse(ExtractOverlayVersion(name), out candidateVersion))
                    continue;

                if (latestAsset == null || candidateVersion > latestVersion ||
                    (candidateVersion == latestVersion &&
                     IsAssetUpdatedMoreRecently(asset, latestAsset)))
                {
                    latestAsset = asset;
                    latestVersion = candidateVersion;
                }
            }

            return latestAsset;
        }

        private static bool IsAssetUpdatedMoreRecently(
            GitHubReleaseAsset candidate,
            GitHubReleaseAsset current)
        {
            DateTime candidateUpdated;
            DateTime currentUpdated;
            return DateTime.TryParse(
                    candidate != null ? candidate.UpdatedAt : null,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out candidateUpdated) &&
                (!DateTime.TryParse(
                    current != null ? current.UpdatedAt : null,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out currentUpdated) || candidateUpdated > currentUpdated);
        }

        private static string GetOverlayCacheDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MMOPlugin",
                "Overlays"
            );
        }

        private static List<OverlayPackageItem> DownloadAndInspectOverlayPackage(
            GitHubReleaseAsset asset,
            string archivePath)
        {
            if (asset == null || string.IsNullOrEmpty(archivePath))
                throw new InvalidDataException("The release overlay archive has no download URL.");

            string cacheRoot = Path.GetDirectoryName(archivePath);
            Directory.CreateDirectory(cacheRoot);
            // Always fetch the selected release asset again. GitHub release assets can
            // be replaced while retaining the same filename and package version.
            if (!string.IsNullOrEmpty(asset.DownloadUrl))
            {
                string downloadPath = archivePath + ".download";
                try
                {
                    using (WebClient client = new WebClient())
                    {
                        client.Headers[HttpRequestHeader.UserAgent] =
                            "MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
                        client.DownloadFile(asset.DownloadUrl, downloadPath);
                    }

                    if (asset.Size > 0 && new FileInfo(downloadPath).Length != asset.Size)
                        throw new InvalidDataException(
                            "The downloaded overlay ZIP size does not match the GitHub release asset."
                        );

                    using (ZipArchive downloadedPackage = ZipFile.OpenRead(downloadPath))
                    {
                        bool containsDashboards = false;
                        foreach (ZipArchiveEntry entry in downloadedPackage.Entries)
                        {
                            if (entry.FullName.EndsWith(
                                    ".simhubdash",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                containsDashboards = true;
                                break;
                            }
                        }
                        if (!containsDashboards)
                            throw new InvalidDataException(
                                "The downloaded release ZIP contains no .simhubdash files."
                            );
                    }

                    if (File.Exists(archivePath))
                        File.Delete(archivePath);
                    File.Move(downloadPath, archivePath);
                }
                finally
                {
                    if (File.Exists(downloadPath))
                        File.Delete(downloadPath);
                }
            }
            else if (!File.Exists(archivePath))
            {
                throw new InvalidDataException("The cached overlay package could not be found.");
            }

            string workRoot = Path.Combine(
                cacheRoot,
                Path.GetFileNameWithoutExtension(archivePath) + "_contents"
            );
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, true);
            Directory.CreateDirectory(workRoot);
            string stagedDirectory = Path.Combine(workRoot, "staged");
            Directory.CreateDirectory(stagedDirectory);
            string templateDirectory = GetSimHubTemplateDirectory();
            List<OverlayPackageItem> items = new List<OverlayPackageItem>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extractedBytes = 0;

            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".simhubdash", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fileName = Path.GetFileName(entry.FullName.Replace('/', '\\'));
                    if (string.IsNullOrWhiteSpace(fileName) ||
                        !names.Add(fileName))
                    {
                        throw new InvalidDataException(
                            "The overlay archive contains an empty or duplicate dashboard filename."
                        );
                    }

                    extractedBytes += entry.Length;
                    if (entry.Length > 100L * 1024L * 1024L ||
                        extractedBytes > 500L * 1024L * 1024L)
                    {
                        throw new InvalidDataException(
                            "The overlay archive exceeds the allowed extraction size."
                        );
                    }

                    string stagedPath = Path.Combine(stagedDirectory, fileName);
                    using (Stream input = entry.Open())
                    using (FileStream output = File.Create(stagedPath))
                        input.CopyTo(output);

                    OverlayIdentity packageIdentity = ReadOverlayIdentity(stagedPath, fileName);
                    InstalledOverlay installedOverlay = FindInstalledOverlay(
                        templateDirectory,
                        fileName,
                        packageIdentity
                    );
                    string stagedHash = ComputeFileHash(stagedPath);
                    bool installed = installedOverlay != null;
                    bool different = installed && IsOverlayUpdateAvailable(
                        packageIdentity.Version,
                        installedOverlay.Version,
                        stagedHash,
                        installedOverlay.Hash
                    );
                    string shownName = packageIdentity.DisplayName;
                    string statusText = !installed
                        ? "Available · v" + (packageIdentity.Version ?? "unknown")
                        : different
                            ? "Update available · v" + (packageIdentity.Version ?? "unknown")
                            : "Up to date · v" + (packageIdentity.Version ?? "unknown");
                    items.Add(new OverlayPackageItem
                    {
                        FileName = fileName,
                        DisplayName = shownName,
                        InstalledDisplayName = installedOverlay != null
                            ? installedOverlay.DisplayName
                            : null,
                        OverlayKey = packageIdentity.Key,
                        PackageVersion = packageIdentity.Version,
                        InstalledVersion = installedOverlay != null ? installedOverlay.Version : null,
                        PackageDirectoryName = GetOverlayPackageDirectoryName(
                            stagedPath,
                            packageIdentity.DisplayName
                        ),
                        StagedPath = stagedPath,
                        InstalledPath = installedOverlay != null ? installedOverlay.Path : null,
                        InstalledExpandedDirectory = installedOverlay != null
                            ? installedOverlay.ExpandedDirectory
                            : null,
                        Hash = stagedHash,
                        IsInstalled = installed,
                        NeedsInstall = !installed,
                        NeedsUpdate = different,
                        StatusText = statusText
                    });
                }
            }

            if (items.Count == 0)
                throw new InvalidDataException("The overlay archive contains no .simhubdash files.");

            return items;
        }

        private static string GetSimHubTemplateDirectory()
        {
            string simHubExecutable = Process.GetCurrentProcess().MainModule.FileName;
            string simHubDirectory = Path.GetDirectoryName(simHubExecutable);
            if (string.IsNullOrEmpty(simHubDirectory))
                throw new InvalidOperationException("Could not locate the SimHub installation directory.");

            return Path.Combine(simHubDirectory, "DashTemplates");
        }

        private static InstalledOverlay FindInstalledOverlay(
            string templateDirectory,
            string fileName,
            OverlayIdentity packageIdentity)
        {
            if (!Directory.Exists(templateDirectory))
                return null;

            // Expanded metadata is the source SimHub uses for the title and version
            // shown in its overlay list. Prefer it over archived dashboard copies.
            InstalledOverlay bestInstalled = null;
            foreach (string metadataPath in Directory.GetFiles(
                templateDirectory,
                "*.djson.metadata",
                SearchOption.AllDirectories))
            {
                OverlayIdentity installedIdentity = ReadOverlayMetadata(metadataPath);
                bool sameName = packageIdentity != null &&
                    !string.IsNullOrEmpty(packageIdentity.Key) &&
                    string.Equals(packageIdentity.Key, installedIdentity.Key, StringComparison.OrdinalIgnoreCase);
                if (sameName)
                {
                    InstalledOverlay candidate = new InstalledOverlay
                    {
                        DisplayName = installedIdentity.DisplayName,
                        ExpandedDirectory = Path.GetDirectoryName(metadataPath),
                        Version = installedIdentity.Version,
                        Hash = ComputeFileHash(metadataPath)
                    };
                    if (bestInstalled == null || IsHigherOverlayVersion(
                            candidate.Version,
                            bestInstalled.Version))
                        bestInstalled = candidate;
                }
            }

            if (bestInstalled != null)
                return bestInstalled;

            // Older SimHub installs may have only the .simhubdash archive available.
            foreach (string path in Directory.GetFiles(
                templateDirectory,
                "*.simhubdash",
                SearchOption.AllDirectories))
            {
                OverlayIdentity installedIdentity = ReadOverlayIdentity(path, Path.GetFileName(path));
                bool sameName = packageIdentity != null &&
                    !string.IsNullOrEmpty(packageIdentity.Key) &&
                    string.Equals(packageIdentity.Key, installedIdentity.Key, StringComparison.OrdinalIgnoreCase);
                bool sameFile = string.Equals(
                    Path.GetFileName(path),
                    fileName,
                    StringComparison.OrdinalIgnoreCase);
                if (sameName || sameFile)
                {
                    InstalledOverlay candidate = new InstalledOverlay
                    {
                        Path = path,
                        DisplayName = installedIdentity.DisplayName,
                        Version = installedIdentity.Version,
                        Hash = ComputeFileHash(path)
                    };
                    if (bestInstalled == null || IsHigherOverlayVersion(
                            candidate.Version,
                            bestInstalled.Version))
                        bestInstalled = candidate;
                }
            }

            return bestInstalled;
        }

        private static bool IsHigherOverlayVersion(string candidateVersion, string currentVersion)
        {
            Version candidate;
            Version current;
            if (!Version.TryParse(candidateVersion, out candidate))
                return false;
            if (!Version.TryParse(currentVersion, out current))
                return true;
            return candidate > current;
        }

        private static string GetOverlayNameWithoutVersion(string displayName)
        {
            string name = Regex.Replace(
                displayName ?? "Overlay",
                "(?i)\\s+v?\\d+\\.\\d+(?:\\.\\d+){1,2}\\s*$",
                ""
            ).Trim();
            return string.IsNullOrWhiteSpace(name) ? "Overlay" : name;
        }

        private static OverlayIdentity ReadOverlayMetadata(string metadataPath)
        {
            string fallbackName = Path.GetFileName(Path.GetDirectoryName(metadataPath));
            OverlayIdentity identity = new OverlayIdentity
            {
                DisplayName = fallbackName,
                Version = ExtractOverlayVersion(fallbackName),
                Key = NormalizeOverlayName(fallbackName)
            };

            string text = File.ReadAllText(metadataPath, Encoding.UTF8);
            Match titleMatch = Regex.Match(
                text,
                "[\\\"']Title[\\\"']\\s*:\\s*[\\\"']([^\\\"']{2,160})[\\\"']",
                RegexOptions.IgnoreCase
            );
            if (titleMatch.Success)
            {
                identity.DisplayName = titleMatch.Groups[1].Value.Trim();
                identity.Key = NormalizeOverlayName(identity.DisplayName);
                identity.Version = ExtractOverlayVersion(identity.DisplayName) ?? identity.Version;
            }

            if (string.IsNullOrEmpty(identity.Version))
            {
                Match versionMatch = Regex.Match(
                    text,
                    "[\\\"']DashboardVersion[\\\"']\\s*:\\s*[\\\"']v?(\\d+\\.\\d+(?:\\.\\d+){1,2})[\\\"']",
                    RegexOptions.IgnoreCase
                );
                if (versionMatch.Success)
                    identity.Version = versionMatch.Groups[1].Value;
            }

            return identity;
        }

        private static string GetOverlayPackageDirectoryName(string packagePath, string displayName)
        {
            try
            {
                using (ZipArchive package = ZipFile.OpenRead(packagePath))
                {
                    foreach (ZipArchiveEntry entry in package.Entries)
                    {
                        if (!entry.FullName.EndsWith(".djson.metadata", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string[] segments = entry.FullName.Split(new[] { '/', '\\' });
                        if (segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[0]))
                            return segments[0];
                    }

                    foreach (ZipArchiveEntry entry in package.Entries)
                    {
                        string[] segments = entry.FullName.Split(new[] { '/', '\\' });
                        if (segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[0]))
                            return segments[0];
                    }
                }
            }
            catch
            {
                // Use the metadata title as a stable folder name if archive inspection fails.
            }

            string name = Regex.Replace(
                displayName ?? "Overlay",
                "(?i)\\s+v?\\d+\\.\\d+(?:\\.\\d+){1,2}\\s*$",
                ""
            ).Trim();
            return string.IsNullOrWhiteSpace(name) ? "Overlay" : name;
        }

        private static OverlayIdentity ReadOverlayIdentity(string packagePath, string fallbackName)
        {
            OverlayIdentity identity = new OverlayIdentity();
            identity.DisplayName = Path.GetFileNameWithoutExtension(fallbackName ?? packagePath ?? "Overlay");
            identity.Version = ExtractOverlayVersion(identity.DisplayName);
            identity.Key = NormalizeOverlayName(identity.DisplayName);

            try
            {
                using (ZipArchive package = ZipFile.OpenRead(packagePath))
                {
                    string title = null;
                    string version = null;

                    // SimHub's .djson.metadata contains the user-visible overlay title.
                    // Prefer it over fields in .djson, which can contain stale/internal names.
                    foreach (ZipArchiveEntry entry in package.Entries)
                    {
                        if (!(entry.FullName ?? "").EndsWith(
                                ".djson.metadata",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        string text;
                        using (Stream stream = entry.Open())
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                            text = reader.ReadToEnd();

                        if (text.Length > 4 * 1024 * 1024)
                            continue;

                        Match metadataTitle = Regex.Match(
                            text,
                            "[\\\"']Title[\\\"']\\s*:\\s*[\\\"']([^\\\"']{2,160})[\\\"']",
                            RegexOptions.IgnoreCase
                        );
                        if (metadataTitle.Success)
                        {
                            string candidate = metadataTitle.Groups[1].Value.Trim();
                            if (candidate.IndexOf("MMO", StringComparison.OrdinalIgnoreCase) >= 0)
                                title = candidate;
                        }

                        if (string.IsNullOrEmpty(title))
                        {
                            MatchCollection titleMatches = Regex.Matches(
                                text,
                                "[\\\"'](?:Name|Title|DashboardName|TemplateName|DisplayName)[\\\"']\\s*:\\s*[\\\"']([^\\\"']{2,160})[\\\"']",
                                RegexOptions.IgnoreCase
                            );
                            foreach (Match match in titleMatches)
                            {
                                string candidate = match.Groups[1].Value.Trim();
                                if (candidate.IndexOf("MMO", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    title = candidate;
                                    break;
                                }
                            }
                        }

                        Match versionMatch = Regex.Match(
                            text,
                            "[\\\"'](?:OverlayVersion|DashboardVersion|Version)[\\\"']\\s*:\\s*[\\\"']v?(\\d+\\.\\d+(?:\\.\\d+){1,2})[\\\"']",
                            RegexOptions.IgnoreCase
                        );
                        if (versionMatch.Success)
                            version = versionMatch.Groups[1].Value;

                        if (!string.IsNullOrEmpty(title))
                            break;
                    }

                    // Fall back to dashboard JSON only when metadata does not expose a title.
                    if (string.IsNullOrEmpty(title))
                    {
                        foreach (ZipArchiveEntry entry in package.Entries)
                        {
                            if (!(entry.FullName ?? "").EndsWith(".djson", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string text;
                            using (Stream stream = entry.Open())
                            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                                text = reader.ReadToEnd();
                            if (text.Length > 4 * 1024 * 1024)
                                continue;

                            MatchCollection titleMatches = Regex.Matches(
                                text,
                                "[\\\"'](?:Name|Title|DashboardName|TemplateName|DisplayName)[\\\"']\\s*:\\s*[\\\"']([^\\\"']{2,160})[\\\"']",
                                RegexOptions.IgnoreCase
                            );
                            foreach (Match match in titleMatches)
                            {
                                string candidate = match.Groups[1].Value.Trim();
                                if (candidate.IndexOf("MMO", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    title = candidate;
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(title))
                                break;
                        }
                    }

                    if (string.IsNullOrEmpty(title) && package.Entries.Count > 0)
                    {
                        string firstFolder = package.Entries[0].FullName.Split(new[] { '/', '\\' })[0];
                        if (firstFolder.IndexOf("MMO", StringComparison.OrdinalIgnoreCase) >= 0)
                            title = firstFolder;
                    }

                    if (!string.IsNullOrEmpty(title))
                        identity.DisplayName = title;
                    identity.Version = ExtractOverlayVersion(identity.DisplayName) ?? version ?? identity.Version;
                    identity.Key = NormalizeOverlayName(identity.DisplayName);
                }
            }
            catch
            {
                // Keep filename-derived identity when a package cannot be read as a SimHub archive.
            }

            return identity;
        }

        private static string ExtractOverlayVersion(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            Match match = Regex.Match(value, "(?i)(?:^|[^a-z0-9])v?(\\d+\\.\\d+(?:\\.\\d+){1,2})(?:$|[^0-9])");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string NormalizeOverlayName(string value)
        {
            string name = Regex.Replace(value ?? "", "(?i)\\bv?\\d+\\.\\d+(?:\\.\\d+){1,2}\\b", "");
            name = Regex.Replace(name, "[^a-zA-Z0-9]+", " ").Trim();
            return name;
        }

        private static bool IsOverlayUpdateAvailable(
            string packageVersion,
            string installedVersion,
            string packageHash,
            string installedHash)
        {
            Version available;
            Version installed;
            if (Version.TryParse(packageVersion, out available) &&
                Version.TryParse(installedVersion, out installed))
                return available > installed;

            return !string.Equals(packageHash, installedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeFileHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder result = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                    result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private async void OverlayRowAction_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            OverlayPackageItem item = button != null
                ? button.Tag as OverlayPackageItem
                : null;
            if (item != null)
                await InstallOverlaysAsync(new List<OverlayPackageItem> { item });
        }

        private async void InstallAllOverlaysButton_Click(object sender, RoutedEventArgs e)
        {
            await InstallOverlaysAsync(new List<OverlayPackageItem>(_overlayItems));
        }

        private async void UpdateAllOverlaysButton_Click(object sender, RoutedEventArgs e)
        {
            await InstallOverlaysAsync(_overlayItems.FindAll(item => item.NeedsUpdate));
        }

        private async Task InstallOverlaysAsync(List<OverlayPackageItem> items)
        {
            if (_installingOverlays || items == null || items.Count == 0)
                return;

            if (!await EnsurePluginIsCurrentAsync())
                return;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null || !File.Exists(items[i].StagedPath))
                {
                    _overlayStatus.Text = "Overlay package is missing. Run Check again.";
                    _overlayStatus.Foreground = Brushes.OrangeRed;
                    return;
                }
            }

            MessageBoxResult confirmation = ShowOverlayRestartConfirmation(items.Count);
            if (confirmation != MessageBoxResult.Yes)
                return;

            try
            {
                string templateDirectory = GetSimHubTemplateDirectory();
                StartOverlayUpdater(items, templateDirectory);
                _installingOverlays = true;
                _overlayStatus.Text = "SimHub will close, install the selected overlays, and restart.";
                _pluginManager.RequestApplicationExit(false);
            }
            catch (Exception ex)
            {
                _overlayStatus.Text = "Could not start overlay installation: " + ex.Message;
                _overlayStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private async Task<bool> EnsurePluginIsCurrentAsync()
        {
            if (_checkingUpdate)
            {
                _overlayStatus.Text = "Checking the plugin version. Try again in a moment.";
                _overlayStatus.Foreground = Brushes.OrangeRed;
                return false;
            }

            await CheckForUpdatesAsync();
            if (!_pluginUpdateStatusKnown)
            {
                _overlayStatus.Text = "Could not verify the plugin version. Overlay installation is blocked.";
                _overlayStatus.Foreground = Brushes.OrangeRed;
                return false;
            }

            if (_pluginUpdateAvailable)
            {
                _overlayStatus.Text = "Update MMO Plugin before installing or updating overlays.";
                _overlayStatus.Foreground = Brushes.OrangeRed;
                return false;
            }

            return true;
        }

        private MessageBoxResult ShowOverlayRestartConfirmation(int count)
        {
            string message = count == 1
                ? "SimHub must close to install this overlay and restart afterward. Continue?"
                : "SimHub must close to install " + count.ToString(CultureInfo.InvariantCulture) +
                    " overlays and restart afterward. Continue?";
            Window owner = Window.GetWindow(this);
            return owner != null
                ? MessageBox.Show(
                    owner,
                    message,
                    "MMO Plugin · Overlays",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                )
                : MessageBox.Show(
                    message,
                    "MMO Plugin · Overlays",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                );
        }

        private void StartOverlayUpdater(
            List<OverlayPackageItem> items,
            string templateDirectory)
        {
            string app = Process.GetCurrentProcess().MainModule.FileName;
            string workDirectory = Path.Combine(Path.GetTempPath(), "MMOPluginOverlays");
            Directory.CreateDirectory(workDirectory);
            string script = Path.Combine(workDirectory, "install-overlays.ps1");
            StringBuilder scriptText = new StringBuilder();
            scriptText.AppendLine("$ErrorActionPreference = 'Stop'");
            scriptText.AppendLine("$processId = [int]$args[0]");
            scriptText.AppendLine("$app = $args[1]");
            scriptText.AppendLine("$work = Split-Path -Parent $app");
            scriptText.AppendLine("$log = Join-Path $env:TEMP 'MMOPluginOverlays\\install-overlays.log'");
            scriptText.AppendLine("while (Get-Process -Id $processId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }");
            scriptText.AppendLine("$operations = @(");
            for (int i = 0; i < items.Count; i++)
            {
                string staged = items[i].StagedPath;
                bool expanded = items[i].NeedsInstall ||
                    !string.IsNullOrEmpty(items[i].InstalledExpandedDirectory);
                string target = expanded
                    ? !string.IsNullOrEmpty(items[i].InstalledExpandedDirectory)
                        ? items[i].InstalledExpandedDirectory
                        : Path.Combine(templateDirectory, items[i].PackageDirectoryName)
                    : !string.IsNullOrEmpty(items[i].InstalledPath)
                        ? items[i].InstalledPath
                        : Path.Combine(templateDirectory, items[i].FileName);
                scriptText.Append("  @{ Staged = '")
                    .Append(EscapePowerShellLiteral(staged))
                    .Append("'; Target = '")
                    .Append(EscapePowerShellLiteral(target))
                    .Append("'; Expanded = $")
                    .Append(expanded ? "true" : "false")
                    .AppendLine(" }");
            }
            scriptText.AppendLine(")");
            scriptText.AppendLine("Add-Type -AssemblyName System.IO.Compression.FileSystem");
            scriptText.AppendLine("$backupRoot = Join-Path $env:TEMP 'MMOPluginOverlays\\backups'");
            scriptText.AppendLine("New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null");
            scriptText.AppendLine("$completed = New-Object System.Collections.ArrayList");
            scriptText.AppendLine("try {");
            scriptText.AppendLine("  foreach ($op in $operations) {");
            scriptText.AppendLine("    $backup = Join-Path $backupRoot ([guid]::NewGuid().ToString('N'))");
            scriptText.AppendLine("    $hadTarget = Test-Path -LiteralPath $op.Target");
            scriptText.AppendLine("    if ($op.Expanded) {");
            scriptText.AppendLine("      $unpack = Join-Path $env:TEMP ('MMOPluginOverlays\\unpack-' + [guid]::NewGuid().ToString('N'))");
            scriptText.AppendLine("      [System.IO.Compression.ZipFile]::ExtractToDirectory($op.Staged, $unpack)");
            scriptText.AppendLine("      $source = $unpack");
            scriptText.AppendLine("      $dirs = @(Get-ChildItem -LiteralPath $unpack -Directory)");
            scriptText.AppendLine("      $files = @(Get-ChildItem -LiteralPath $unpack -File)");
            scriptText.AppendLine("      if ($dirs.Count -eq 1 -and $files.Count -eq 0) { $source = $dirs[0].FullName }");
            scriptText.AppendLine("      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $op.Target) | Out-Null");
            scriptText.AppendLine("      if ($hadTarget) { Move-Item -LiteralPath $op.Target -Destination $backup -Force }");
            scriptText.AppendLine("      try { Move-Item -LiteralPath $source -Destination $op.Target -Force } catch { if ($hadTarget -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $op.Target -Force }; throw }");
            scriptText.AppendLine("      [void]$completed.Add(@{ Target = $op.Target; Backup = $backup; HadTarget = $hadTarget; Expanded = $true })");
            scriptText.AppendLine("    } else {");
            scriptText.AppendLine("      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $op.Target) | Out-Null");
            scriptText.AppendLine("      if ($hadTarget) { Copy-Item -LiteralPath $op.Target -Destination $backup -Force }");
            scriptText.AppendLine("      Copy-Item -LiteralPath $op.Staged -Destination $op.Target -Force");
            scriptText.AppendLine("      [void]$completed.Add(@{ Target = $op.Target; Backup = $backup; HadTarget = $hadTarget; Expanded = $false })");
            scriptText.AppendLine("    }");
            scriptText.AppendLine("  }");
            scriptText.AppendLine("} catch {");
            scriptText.AppendLine("  Add-Content -LiteralPath $log -Value $_.Exception.ToString()");
            scriptText.AppendLine("  for ($i = $completed.Count - 1; $i -ge 0; $i--) {");
            scriptText.AppendLine("    $done = $completed[$i]");
            scriptText.AppendLine("    if ($done.Expanded -and (Test-Path -LiteralPath $done.Target)) { Remove-Item -LiteralPath $done.Target -Recurse -Force }");
            scriptText.AppendLine("    elseif (-not $done.Expanded -and -not $done.HadTarget -and (Test-Path -LiteralPath $done.Target)) { Remove-Item -LiteralPath $done.Target -Force }");
            scriptText.AppendLine("    if ($done.HadTarget -and (Test-Path -LiteralPath $done.Backup)) { Move-Item -LiteralPath $done.Backup -Destination $done.Target -Force }");
            scriptText.AppendLine("  }");
            scriptText.AppendLine("  Start-Process -FilePath $app -WorkingDirectory $work");
            scriptText.AppendLine("  exit 1");
            scriptText.AppendLine("}");
            scriptText.AppendLine("Start-Process -FilePath $app -WorkingDirectory $work");

            File.WriteAllText(script, scriptText.ToString(), new UTF8Encoding(false));
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell\\v1.0\\powershell.exe"
                ),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
                    QuoteArgument(script) + " " +
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + " " +
                    QuoteArgument(app),
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(start);
        }

        private static string EscapePowerShellLiteral(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_checkingUpdate)
                return;
            _checkingUpdate = true;
            _pluginUpdateStatusKnown = false;
            _pluginUpdateAvailable = false;
            _checkUpdateButton.IsEnabled = false;
            _updateStatus.Text = "Checking GitHub releases…";
            _updateStatus.Foreground = Brushes.White;
            _installUpdateButton.Visibility = Visibility.Collapsed;
            RefreshOverlayList();
            try
            {
                GitHubRelease release = await Task.Run(LoadLatestRelease);
                Version available;
                Version installed;
                if (release == null ||
                    (!TryParseVersion(release.TagName, out available) &&
                     !TryParseVersion(release.Name, out available)))
                {
                    _updateStatus.Text = "No usable release found. Open GitHub releases.";
                    _latestRelease = null;
                    _pluginUpdateStatusKnown = false;
                    SetUpdateBadge(_pluginUpdateBadge, false);
                    return;
                }
                _latestRelease = release;
                Version.TryParse(MMOPlugin.PluginVersion, out installed);
                if (installed == null || available > installed)
                {
                    _pluginUpdateStatusKnown = true;
                    _pluginUpdateAvailable = true;
                    SetUpdateBadge(_pluginUpdateBadge, true);
                    _updateStatus.Foreground = Brushes.OrangeRed;
                    _updateStatus.Text = "Update available · v" + available.ToString();
                    _installUpdateButton.Visibility = Visibility.Visible;
                    _installUpdateButton.IsEnabled = FindPluginAsset(release) != null;
                    if (!_installUpdateButton.IsEnabled)
                        _updateStatus.Text += " · release has no MMOPlugin.dll/.zip asset";
                }
                else
                {
                    _pluginUpdateStatusKnown = true;
                    _pluginUpdateAvailable = false;
                    SetUpdateBadge(_pluginUpdateBadge, false);
                    _updateStatus.Text = "Already up to date · v" + MMOPlugin.PluginVersion;
                    _updateStatus.Foreground = Brushes.LightGreen;
                    _latestRelease = null;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    response.Close();
                    _updateStatus.Text = "No public GitHub release found yet. See GitHub releases.";
                    _updateStatus.Foreground = Brushes.White;
                    _latestRelease = null;
                    _pluginUpdateStatusKnown = false;
                }
                else
                {
                    _updateStatus.Text = "Could not check for updates: " + ex.Message;
                    _updateStatus.Foreground = Brushes.White;
                    _latestRelease = null;
                    _pluginUpdateStatusKnown = false;
                }
            }
            catch (Exception ex)
            {
                _updateStatus.Text = "Could not check for updates: " + ex.Message;
                _updateStatus.Foreground = Brushes.White;
                _latestRelease = null;
                _pluginUpdateStatusKnown = false;
            }
            finally
            {
                _checkingUpdate = false;
                _checkUpdateButton.IsEnabled = true;
                RefreshOverlayList();
            }
        }

        private static GitHubRelease LoadLatestRelease()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
            request.UserAgent = "MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                return (GitHubRelease)serializer.ReadObject(stream);
            }
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            string normalized = (text ?? "").Trim();
            while (normalized.Length > 0 && (normalized[0] == 'v' || normalized[0] == 'V'))
                normalized = normalized.Substring(1);
            int suffix = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
                normalized = normalized.Substring(0, suffix);
            if (Version.TryParse(normalized, out version))
                return true;

            Match match = Regex.Match(
                text ?? "",
                @"(?<!\d)v?(\d+\.\d+(?:\.\d+){0,2})",
                RegexOptions.IgnoreCase
            );
            return match.Success && Version.TryParse(match.Groups[1].Value, out version);
        }

        private static GitHubReleaseAsset FindPluginAsset(GitHubRelease release)
        {
            if (release == null || release.Assets == null)
                return null;
            foreach (GitHubReleaseAsset asset in release.Assets)
            {
                string name = asset.Name ?? "";
                if (string.Equals(name, "MMOPlugin.dll", StringComparison.OrdinalIgnoreCase) ||
                    (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                     name.IndexOf("MMOPlugin", StringComparison.OrdinalIgnoreCase) >= 0))
                    return asset;
            }
            return null;
        }

        private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_installingUpdate || _latestRelease == null)
                return;
            GitHubReleaseAsset asset = FindPluginAsset(_latestRelease);
            if (asset == null)
                return;
            _installingUpdate = true;
            _installUpdateButton.IsEnabled = false;
            _checkUpdateButton.IsEnabled = false;
            _updateStatus.Text = "Downloading update…";
            try
            {
                string target = typeof(MMOPlugin).Assembly.Location;
                string tempDirectory = Path.Combine(Path.GetTempPath(), "MMOPluginUpdate");
                Directory.CreateDirectory(tempDirectory);
                string downloaded = Path.Combine(tempDirectory, Path.GetFileName(asset.Name));
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
                    await client.DownloadFileTaskAsync(new Uri(asset.DownloadUrl), downloaded);
                }
                string stagedDll = downloaded;
                if (downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string extractDirectory = Path.Combine(tempDirectory, "package");
                    if (Directory.Exists(extractDirectory))
                        Directory.Delete(extractDirectory, true);
                    ZipFile.ExtractToDirectory(downloaded, extractDirectory);
                    string[] dlls = Directory.GetFiles(extractDirectory, "MMOPlugin.dll", SearchOption.AllDirectories);
                    if (dlls.Length == 0)
                        throw new InvalidDataException("The release archive does not contain MMOPlugin.dll.");
                    stagedDll = dlls[0];
                }
                if (!File.Exists(stagedDll) || !string.Equals(Path.GetFileName(stagedDll), "MMOPlugin.dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded release is not an MMOPlugin.dll or supported ZIP package.");

                MessageBoxResult confirmation = ShowUpdateRestartConfirmation();
                if (confirmation != MessageBoxResult.Yes)
                {
                    _updateStatus.Text = "Update downloaded. SimHub will restart only after you confirm.";
                    _installingUpdate = false;
                    _installUpdateButton.IsEnabled = true;
                    _checkUpdateButton.IsEnabled = true;
                    return;
                }

                StartUpdater(stagedDll, target);
                _updateStatus.Text = "Update downloaded. SimHub will restart to install it.";
                _pluginManager.RequestApplicationExit(false);
            }
            catch (Exception ex)
            {
                _updateStatus.Text = "Update failed: " + ex.Message;
                _installingUpdate = false;
                _installUpdateButton.IsEnabled = true;
                _checkUpdateButton.IsEnabled = true;
            }
        }

        private MessageBoxResult ShowUpdateRestartConfirmation()
        {
            const string message =
                "The update has been downloaded. SimHub must close and restart to install it. Continue?";
            Window owner = Window.GetWindow(this);
            return owner != null
                ? MessageBox.Show(
                    owner,
                    message,
                    "MMO Plugin update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                )
                : MessageBox.Show(
                    message,
                    "MMO Plugin update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                );
        }

        private void StartUpdater(string stagedDll, string targetDll)
        {
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            string script = Path.Combine(Path.GetTempPath(), "MMOPluginUpdate", "install-update.ps1");
            string scriptText =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$processId = [int]$args[0]\r\n$staged = $args[1]\r\n$target = $args[2]\r\n$app = $args[3]\r\n" +
                "try { Wait-Process -Id $processId -Timeout 120 } catch {}\r\n" +
                "Start-Sleep -Milliseconds 700\r\n" +
                "$backup = $target + '.bak'\r\nif (Test-Path $backup) { Remove-Item -LiteralPath $backup -Force }\r\n" +
                "Copy-Item -LiteralPath $target -Destination $backup -Force\r\n" +
                "Copy-Item -LiteralPath $staged -Destination $target -Force\r\n" +
                "$work = Split-Path -Parent $app\r\n" +
                "Start-Process -FilePath $app -WorkingDirectory $work\r\n";
            File.WriteAllText(script, scriptText, new UTF8Encoding(false));
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + QuoteArgument(script) +
                    " " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                    " " + QuoteArgument(stagedDll) + " " + QuoteArgument(targetDll) + " " + QuoteArgument(executable),
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(start);
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }
    }

    [DataContract]
    internal sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }
        [DataMember(Name = "name")]
        public string Name { get; set; }
        [DataMember(Name = "assets")]
        public GitHubReleaseAsset[] Assets { get; set; }
    }

    [DataContract]
    internal sealed class GitHubReleaseAsset
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }
        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }
        [DataMember(Name = "updated_at")]
        public string UpdatedAt { get; set; }
        [DataMember(Name = "size")]
        public long Size { get; set; }
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
