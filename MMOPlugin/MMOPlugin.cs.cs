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
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Win32;
using GameReaderCommon;
using SimHub.Plugins;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
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
        public const string PluginVersion = "0.4.2.1";
        private static readonly ImageSource PluginLogoImage = CreatePluginLogoImage();
        private static readonly string PluginDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimHub",
            "PluginsData",
            "MMOPlugin"
        );
        private static readonly string SettingsFilePath = Path.Combine(PluginDataDirectory, "settings.ini");
        private readonly CarTrackStatisticsStore _statisticsStore =
            new CarTrackStatisticsStore(Path.Combine(PluginDataDirectory, "car-track-data.json"));

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle
        {
            get { return "MMO Plugin"; }
        }

        public ImageSource PictureIcon
        {
            get { return PluginLogoImage; }
        }

        private static ImageSource CreatePluginLogoImage()
        {
            System.Windows.Media.Imaging.BitmapImage image =
                new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(
                "pack://application:,,,/MMOPlugin;component/rsz_logo_sidebar.png",
                UriKind.Absolute
            );
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private const int MaxVehicles = 62;
        private const int PropertyReadSampleRate = 128;
        [ThreadStatic]
        private static int _propertyReadSampleSequence;
        private readonly object _dataSnapshotLock = new object();

        private readonly LMUSharedMemory _sharedMemory =
            new LMUSharedMemory();

        private readonly LMUStandingsApi _standingsApi =
            new LMUStandingsApi();

        private readonly LMUVehicleScoring[] _vehiclesByPosition =
            new LMUVehicleScoring[MaxVehicles];

        private readonly bool[] _hasVehicle =
            new bool[MaxVehicles];

        private readonly int[] _lastKnownVehicleIdByPosition =
            CreateVehicleIdArray();
        private readonly DateTime[] _lastKnownVehicleSeenUtcByPosition =
            new DateTime[MaxVehicles];
        private static readonly TimeSpan SharedMemoryGracePeriod =
            TimeSpan.FromSeconds(3.0);
        private readonly int[] _positionGainByPosition =
            new int[MaxVehicles];
        private readonly int[] _raceGridCarIds = CreateVehicleIdArray();
        private readonly int[] _raceGridPositions = new int[MaxVehicles];
        private readonly int[] _raceGridCandidateCarIds = CreateVehicleIdArray();
        private readonly int[] _raceGridCandidatePositions = new int[MaxVehicles];
        private readonly List<RaceStartPosition> _raceGridSaveBuffer =
            new List<RaceStartPosition>(MaxVehicles);
        private readonly Dictionary<int, int> _raceStartPositionByCarId =
            new Dictionary<int, int>(MaxVehicles);
        private int _raceGridCount;
        private string _raceGridTrackName = "";
        private int _raceGridSession = -1;
        private double _raceGridStartET = double.NaN;
        private bool _raceGridFrozen;
        private bool _raceStartSnapshotLoaded;
        private readonly Dictionary<int, PreviousPositionState> _previousPositionByVehicleId =
            new Dictionary<int, PreviousPositionState>();
        private readonly HashSet<string> _syncedVehicleModelIdentities =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private LMUScoringInfo _scoringInfo;

        private bool _hasScoringInfo;
        private volatile bool _showTeamNamesInsteadOfDriverNames;
        private volatile bool _additionalTeamNamesInRelative;
        private volatile int _tyreCompoundSymbolMode;
        private volatile int _lastLaptimeDisplayDurationMs = 10000;
        private volatile bool _showLastPitLapAndTimeInLeaderboard;
        private volatile bool _showLastPitLapAndTimeInRelative;
        private volatile bool _showPositionsGained;
        private volatile int _positionChangeColorCodingDurationSeconds = 3;
        private volatile bool _debuggingEnabled;
        private readonly TeamSpeakClientQuery _teamSpeak = new TeamSpeakClientQuery();

        private readonly double[] _relativeAhead = new double[3];
        private readonly double[] _relativeBehind = new double[3];
        private readonly double[] _relativeAheadScratch = new double[3];
        private readonly double[] _relativeBehindScratch = new double[3];
        private readonly List<RelativeVehicle> _relativeAheadCandidates =
            new List<RelativeVehicle>(MaxVehicles);
        private readonly List<RelativeVehicle> _relativeBehindCandidates =
            new List<RelativeVehicle>(MaxVehicles);
        private readonly int[] _incidentsByPosition = new int[MaxVehicles];
        private readonly Dictionary<int, VehicleTyreState> _tyreStateByVehicleId =
            new Dictionary<int, VehicleTyreState>();
        private readonly Dictionary<int, LMUVehicleTelemetryRelative> _telemetryByVehicleId =
            new Dictionary<int, LMUVehicleTelemetryRelative>(MaxMappedVehicles);
        private readonly Dictionary<int, LMUVehicleTelemetryRelative> _relativeTelemetryByVehicleId =
            new Dictionary<int, LMUVehicleTelemetryRelative>(MaxMappedVehicles);
        private const int TelemetrySnapshotIntervalMilliseconds = 50;
        private long _lastTelemetrySnapshotTimestamp;
        private bool _hasTelemetrySnapshot;
        private LMUVehicleTelemetryRelative _playerTelemetryForTrackLimits;
        private bool _hasPlayerTelemetryForTrackLimits;
        private int _playerTelemetryIndexForTrackLimits = -1;
        private bool _playerHasVehicleForTrackLimits;
        private bool _hasPlayerTelemetryHeaderForTrackLimits;
        private readonly Dictionary<int, LastLapTimingState> _lastLapTimingByVehicleId =
            new Dictionary<int, LastLapTimingState>();
        private readonly Dictionary<int, PitStopTimingState> _pitStopTimingByVehicleId =
            new Dictionary<int, PitStopTimingState>();
        private readonly Dictionary<int, StintLapState> _stintLapByVehicleId =
            new Dictionary<int, StintLapState>();
        private double _playerIncidents;
        private double _incidentsLimit;
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

        private sealed class PreviousPositionState
        {
            public int CurrentPosition;
            public int PreviousPosition;
            public DateTime ChangeTimeUtc;
            public DateTime HoldUntilUtc;
        }

        private sealed class PitStopTimingState
        {
            public bool IsMeasuring;
            public bool WasInPitLane;
            public bool WasInGarage;
            public bool HasStoppedThisPitVisit;
            public bool ResetOnNextStop;
            public bool HasPitEntryLap;
            public bool GarageExitPending;
            public bool RacePitStartPending;
            public bool RaceGreenFlagBaselinePending;
            public bool UpdatePitLapOnGarageExit;
            public double PitStopStartET;
            public double AccumulatedPitTimeSeconds;
            public int PitEntryLap;
            public int PitLap;
            public double LastPitTimeSeconds;
        }

        private sealed class StintLapState
        {
            public bool Initialized;
            public bool Active;
            public bool WaitingForRaceStart;
            public bool WasInPitLane;
            public bool WasInGarage;
            public bool HasPitBaseline;
            public bool ServiceDetected;
            public int FuelServiceEvidenceSamples;
            public int TyreServiceEvidenceSamples;
            public int CompoundServiceEvidenceSamples;
            public int LastCompletedLap;
            public int StintLap;
            public double PitEntryFuel = double.NaN;
            public double PitEntryWear = double.NaN;
            public byte[] PitEntryCompounds;
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
            public byte[] CachedVehicleFileBytes;
            public byte[] CachedVehicleNameBytes;
            public byte[] CachedVehicleModelBytes;
            public byte[] CachedTrackNameBytes;
            public string CachedVehicleFile = "";
            public string CachedVehicleName = "";
            public string CachedVehicleModel = "";
            public string CachedTrackName = "";
            public string CachedCarModelName = "";
            public string CachedApiCarClass = "";
            public string CachedApiCarNumber = "";
            public string CachedApiTeamName = "";
            public int CachedVehicleMetadataVersion = -1;
            public bool VehicleMetadataChecked;
            public bool HasVehicleMetadata;
            public string CachedManufacturer = "";
            public string CachedMetadataModel = "";
            public string StatisticsKey = "";
            public string StatisticsCarKey = "";
            public string StatisticsCarName = "";
            public string StatisticsManufacturer = "";
            public string StatisticsModel = "";
            public string StatisticsTrackName = "";
            public double FuelTarget;
            public bool HasFuelTarget;
            public bool HasVirtualEnergy;
            public bool HasVirtualEnergyStored;
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

        private void AttachSafeDelegate(string name, Func<object> valueProvider)
        {
            this.AttachDelegate(
                name: name,
                valueProvider: () =>
                {
                    bool sampled = PluginPerformanceDiagnostics.Enabled &&
                        (++_propertyReadSampleSequence & (PropertyReadSampleRate - 1)) == 0;
                    long performanceStartedAt = sampled ? Stopwatch.GetTimestamp() : 0L;
                    long allocatedBytesAtStart = sampled
                        ? GC.GetAllocatedBytesForCurrentThread()
                        : -1L;
                    try
                    {
                        lock (_dataSnapshotLock)
                        {
                            object value = valueProvider == null ? null : valueProvider();
                            if (value == null)
                                return IsTextProperty(name) ? (object)string.Empty : 0;

                            if (value is double)
                            {
                                double number = (double)value;
                                if (double.IsNaN(number) || double.IsInfinity(number)) return 0.0;
                            }
                            else if (value is float)
                            {
                                float number = (float)value;
                                if (float.IsNaN(number) || float.IsInfinity(number)) return 0.0f;
                            }
                            return value;
                        }
                    }
                    finally
                    {
                        if (sampled)
                            PluginPerformanceDiagnostics.PropertyGetter.AddSampled(
                                performanceStartedAt,
                                allocatedBytesAtStart,
                                PropertyReadSampleRate
                            );
                    }
                }
            );
        }

        private static bool IsTextProperty(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name == "MMO_ConnectionStatus" ||
                   name == "MMO_TrackName" ||
                   name == "MMO_ApiConnectionStatus" ||
                   name == "MMO_ApiLastError" ||
                   name == "MMO_Teamspeak_Status" ||
                   name == "MMO_Teamspeak_ServerName" ||
                   name == "MMO_Teamspeak_ChannelName" ||
                   name == "MMO_Teamspeak_PlayerName" ||
                   name.EndsWith("_Name", StringComparison.Ordinal) ||
                   name.EndsWith("CarNumber", StringComparison.Ordinal) ||
                   name.EndsWith("CarClass", StringComparison.Ordinal) ||
                   name.EndsWith("DriverName", StringComparison.Ordinal) ||
                   name.EndsWith("DriveNameRelative", StringComparison.Ordinal) ||
                   name.EndsWith("TeamName", StringComparison.Ordinal) ||
                   name.EndsWith("PitStatus", StringComparison.Ordinal) ||
                   name.EndsWith("PitTime", StringComparison.Ordinal) ||
                   name.IndexOf("TyreComp", StringComparison.Ordinal) >= 0;
        }

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            LoadPluginSettings();
            PluginPerformanceDiagnostics.SetEnabled(_debuggingEnabled);
            try { _statisticsStore.LoadOrCreate(); }
            catch { /* Keep the plugin usable if local statistics cannot be read or created. */ }

            _standingsApi.VehicleMetadataUpdated += SyncSavedVehicleMetadata;
            _standingsApi.Start();
            this.AddInputMapping(
                inputName: "FuelTargetIncrease",
                inputPressed: (manager, input) => AdjustFuelTarget(0.02),
                inputReleased: (manager, input) => { }
            );
            this.AddInputMapping(
                inputName: "FuelTargetDecrease",
                inputPressed: (manager, input) => AdjustFuelTarget(-0.02),
                inputReleased: (manager, input) => { }
            );
            LoadTeamSpeakApiKey();
            ClearTrackLimitPoints();

            AttachSafeDelegate(
                name: "MMO_ConnectionStatus",
                valueProvider: () =>
                    _sharedMemory.IsConnected
                        ? "Connected to LMU"
                        : "Waiting for LMU"
            );

            AttachSafeDelegate(
                name: "MMO_TrackName",
                valueProvider: () =>
                    _hasScoringInfo
                        ? DecodeString(_scoringInfo.mTrackName)
                        : ""
            );

            AttachSafeDelegate(
                name: "MMO_VehicleCount",
                valueProvider: () =>
                    _hasScoringInfo
                        ? _scoringInfo.mNumVehicles
                        : 0
            );

            AttachSafeDelegate(
                name: "MMO_ApiConnectionStatus",
                valueProvider: () =>
                    _standingsApi.IsConnected
                        ? "Connected to LMU API"
                        : "Waiting for LMU API"
            );

            AttachSafeDelegate(
                name: "MMO_ApiLastError",
                valueProvider: () =>
                    _standingsApi.LastError
            );

            AttachSafeDelegate(
                name: "MMO_SettingsShowTeamNamesInLeaderboard",
                valueProvider: () => _showTeamNamesInsteadOfDriverNames ? 1 : 0
            );

            AttachSafeDelegate(
                name: "MMO_SettingsAdditionalTeamNamesInRelative",
                valueProvider: () => _additionalTeamNamesInRelative ? 1 : 0
            );

            AttachSafeDelegate(
                name: "MMO_SettingsTyreCompoundSymbol",
                valueProvider: () => _tyreCompoundSymbolMode
            );

            AttachSafeDelegate(
                name: "MMO_SettingsLastLaptimeDisplayDuration",
                valueProvider: () => _lastLaptimeDisplayDurationMs
            );

            AttachSafeDelegate(
                name: "MMO_SettingsLeaderboardShowLastPitLapAndTimeDuringRace",
                valueProvider: () => _showLastPitLapAndTimeInLeaderboard ? 1 : 0
            );

            AttachSafeDelegate(
                name: "MMO_SettingsLeaderboardShowPositionsGained",
                valueProvider: () => _showPositionsGained ? 1 : 0
            );

            AttachSafeDelegate(
                name: "MMO_SettingsLeaderboardPositionChangeColorCoding",
                valueProvider: () => _positionChangeColorCodingDurationSeconds
            );

            AttachSafeDelegate(
                name: "MMO_SettingsRelativeShowLastPitLapAndTimeDuringRace",
                valueProvider: () => _showLastPitLapAndTimeInRelative ? 1 : 0
            );

            AttachSafeDelegate("MMO_Teamspeak_Connected", () => _teamSpeak.Connected ? 1 : 0);
            AttachSafeDelegate("MMO_Teamspeak_Status", () => _teamSpeak.Status);
            AttachSafeDelegate("MMO_Teamspeak_ServerName", () => _teamSpeak.ServerName);
            AttachSafeDelegate("MMO_Teamspeak_ChannelName", () => _teamSpeak.ChannelName);
            AttachSafeDelegate("MMO_Teamspeak_PlayerName", () => _teamSpeak.PlayerName);
            AttachSafeDelegate("MMO_Teamspeak_PlayerStatus", () => _teamSpeak.PlayerStatus);
            AttachSafeDelegate("MMO_Teamspeak_PlayerTalking", () => _teamSpeak.PlayerTalking ? 1 : 0);
            AttachSafeDelegate("MMO_Teamspeak_ClientCount", () => _teamSpeak.ClientCount);
            for (int i = 1; i <= 15; i++)
            {
                int slot = i;
                AttachSafeDelegate("MMO_Teamspeak_" + slot + "_Name", () => _teamSpeak.GetClientName(slot));
                AttachSafeDelegate("MMO_Teamspeak_" + slot + "_Status", () => _teamSpeak.GetClientStatus(slot));
                AttachSafeDelegate("MMO_Teamspeak_" + slot + "_Talking", () => _teamSpeak.GetClientTalking(slot) ? 1 : 0);
            }

            AttachSafeDelegate(
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

            AttachSafeDelegate(
                name: "MMO_PlayerInc",
                valueProvider: () => _playerIncidents
            );

            // -1 means the session's configured track-limit threshold is not available
            // through the shared-memory fields currently mapped by this plugin.
            AttachSafeDelegate(
                name: "MMO_maxIncidents",
                valueProvider: () => _incidentsLimit
            );

            AttachSafeDelegate(
                name: "MMO_RelativeAhead1",
                valueProvider: () => _relativeAhead[0]
            );

            AttachSafeDelegate(
                name: "MMO_RelativeAhead2",
                valueProvider: () => _relativeAhead[1]
            );

            AttachSafeDelegate(
                name: "MMO_RelativeAhead3",
                valueProvider: () => _relativeAhead[2]
            );

            AttachSafeDelegate(
                name: "MMO_RelativeBehind1",
                valueProvider: () => _relativeBehind[0]
            );

            AttachSafeDelegate(
                name: "MMO_RelativeBehind2",
                valueProvider: () => _relativeBehind[1]
            );

            AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "prevPosition",
                    valueProvider: () => GetPreviousPositionAtPosition(capturedPosition)
                );

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "PositionGain",
                    valueProvider: () => _positionGainByPosition[capturedPosition - 1]
                );

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "TyreCompFL",
                    valueProvider: () => GetTyreCompound(capturedPosition, 0)
                );

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "TyreCompFR",
                    valueProvider: () => GetTyreCompound(capturedPosition, 1)
                );

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "TyreCompRL",
                    valueProvider: () => GetTyreCompound(capturedPosition, 2)
                );

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "TyreCompRR",
                    valueProvider: () => GetTyreCompound(capturedPosition, 3)
                );

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "LastLaptime",
                    valueProvider: () => GetLastLapTime(capturedPosition)
                );

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "LastLaptimePopup",
                    valueProvider: () => GetLastLaptimePopup(capturedPosition)
                );

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "PitLap",
                    valueProvider: () => GetPitStopState(capturedPosition).PitLap
                );

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "PitTime",
                    valueProvider: () => GetPitTimeSeconds(
                        GetPitStopState(capturedPosition).LastPitTimeSeconds
                    )
                );

                AttachSafeDelegate(
                    name: "MMO_Position" + positionName + "StintLap",
                    valueProvider: () => GetStintLap(capturedPosition)
                );

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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

                AttachSafeDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Incidents",
                    valueProvider: () =>
                        _incidentsByPosition[capturedPosition - 1]
                );

                AttachSafeDelegate(
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

                AttachSafeDelegate(
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
            _hasTelemetrySnapshot = false;
            _hasPlayerTelemetryForTrackLimits = false;
            _telemetryByVehicleId.Clear();
            for (int i = 0; i < MaxVehicles; i++)
            {
                _hasVehicle[i] = false;
                _vehiclesByPosition[i] = new LMUVehicleScoring();
            }
        }

        private void RegisterPlayerProperties()
        {
            AttachSafeDelegate("MMO_PlayerRelativeAhead1", () => _relativeAhead[0]);
            AttachSafeDelegate("MMO_PlayerRelativeAhead2", () => _relativeAhead[1]);
            AttachSafeDelegate("MMO_PlayerRelativeAhead3", () => _relativeAhead[2]);
            AttachSafeDelegate("MMO_PlayerRelativeBehind1", () => _relativeBehind[0]);
            AttachSafeDelegate("MMO_PlayerRelativeBehind2", () => _relativeBehind[1]);
            AttachSafeDelegate("MMO_PlayerRelativeBehind3", () => _relativeBehind[2]);

            AttachSafeDelegate("MMO_PlayerOverall", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null ? vehicle.Position : position;
            });
            AttachSafeDelegate("MMO_PlayerClass", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.PositionClass : 0;
            });
            AttachSafeDelegate("MMO_PlayerCarNumber", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.CarNumber ?? "" : "";
            });
            AttachSafeDelegate("MMO_PlayerCarClass", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.CarClass ?? "" : "";
            });

            string[] wheelNames = { "FL", "FR", "RL", "RR" };
            for (int wheel = 0; wheel < wheelNames.Length; wheel++)
            {
                int capturedWheel = wheel;
                AttachSafeDelegate(
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

            AttachSafeDelegate("MMO_PlayerDriverName", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                if (vehicle == null) return "";
                return _showTeamNamesInsteadOfDriverNames
                    ? vehicle.TeamName ?? ""
                    : FormatDriverName(vehicle.DriverName);
            });
            AttachSafeDelegate("MMO_PlayerDriveNameRelative", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                if (vehicle == null) return "";
                string driverName = vehicle.DriverName ?? "";
                return _additionalTeamNamesInRelative
                    ? FormatRelativeDriverName(driverName, vehicle.TeamName)
                    : driverName;
            });
            AttachSafeDelegate("MMO_PlayerTeamName", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.TeamName ?? "" : "";
            });
            AttachSafeDelegate("MMO_PlayerBestLaptime", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.BestLapTime : 0.0;
            });
            AttachSafeDelegate("MMO_PlayerLastLaptime", () =>
            {
                return GetLastLapTimeForPlayer();
            });
            AttachSafeDelegate("MMO_PlayerGapToLeader", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null
                    ? GetGapValue(vehicle.GapToLeader, GetLapsBehindClassLeader(position, vehicle))
                    : 0.0;
            });
            AttachSafeDelegate("MMO_PlayerIntervalToNext", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null
                    ? GetGapValue(vehicle.IntervalToNext, GetLapsBehindNext(position))
                    : 0.0;
            });
            AttachSafeDelegate("MMO_PlayerPitStatus", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return GetPitStatus(position, vehicle);
            });
            AttachSafeDelegate("MMO_PlayerPitLap", () =>
                GetPitStopStateByVehicleId(_lastPlayerVehicleId).PitLap
            );
            AttachSafeDelegate("MMO_PlayerPitTime", () =>
                GetPitTimeSeconds(
                    GetPitStopStateByVehicleId(_lastPlayerVehicleId).LastPitTimeSeconds
                )
            );
            AttachSafeDelegate("MMO_PlayerStintLap", () =>
                GetStintLapByVehicleId(_lastPlayerVehicleId)
            );
            AttachSafeDelegate("MMO_PlayerVirtualEnergy", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null
                    ? GetVirtualEnergy(vehicle.VeFraction)
                    : 0.0;
            });
            AttachSafeDelegate("MMO_PlayerVELap", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)_playerConsumption.LastVePerLap
                    : "");
            AttachSafeDelegate("MMO_PlayerFuelLap", () => _playerConsumption.LastFuelPerLap);
            AttachSafeDelegate("MMO_PlayerFuelTarget", () => _playerConsumption.FuelTarget);
            AttachSafeDelegate("MMO_PlayerVEAvg3Laps", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)AverageLastLaps(_playerConsumption.VeLapHistory)
                    : "");
            AttachSafeDelegate("MMO_PlayerVELapsLeft", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)CalculateLapsLeft(
                        GetCurrentVeLevel(),
                        AverageLastLaps(_playerConsumption.VeLapHistory))
                    : "");
            AttachSafeDelegate("MMO_PlayerFuelLapsLeft", () =>
                CalculateLapsLeft(
                    GetCurrentFuelLevel(),
                    AverageLastLaps(_playerConsumption.FuelLapHistory)));
            AttachSafeDelegate("MMO_PlayerRefuelVE", () =>
                GetCurrentVeLevel() > 0.0
                    ? (object)GetVeNeededToFinish()
                    : "");
            AttachSafeDelegate("MMO_PlayerPenalties", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.Penalties : 0;
            });
            AttachSafeDelegate("MMO_PlayerIncidents", () => _playerIncidents);
            AttachSafeDelegate("MMO_IncidentsLimit", () => _incidentsLimit);
            AttachSafeDelegate("MMO_PlayerInvalidLap", () =>
            {
                int position;
                LMUVehicleScoring scoring;
                return TryGetPlayerScoring(out position, out scoring) && scoring.mLastLapTime < 0.0
                    ? 1
                    : 0;
            });
            AttachSafeDelegate("MMO_PlayerFinishStatus", () =>
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
                _showPositionsGained,
                SetShowPositionsGained,
                _positionChangeColorCodingDurationSeconds,
                SetPositionChangeColorCodingDuration,
                _showLastPitLapAndTimeInRelative,
                SetShowLastPitLapAndTimeInRelative,
                _lastLaptimeDisplayDurationMs,
                SetLastLaptimeDisplayDuration,
                GetTeamSpeakApiKey,
                SetTeamSpeakApiKey,
                _debuggingEnabled,
                SetDebuggingEnabled,
                _statisticsStore
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
            _showPositionsGained = false;
            _positionChangeColorCodingDurationSeconds = 3;
            _debuggingEnabled = false;
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

                    if (string.Equals(key, "LeaderboardShowPositionsGained", StringComparison.OrdinalIgnoreCase))
                    {
                        bool showPositionsGained;
                        if (bool.TryParse(settingValue, out showPositionsGained))
                            _showPositionsGained = showPositionsGained;
                        continue;
                    }

                    if (string.Equals(key, "LeaderboardPositionChangeColorCodingSeconds", StringComparison.OrdinalIgnoreCase))
                    {
                        int durationSeconds;
                        if (int.TryParse(settingValue, out durationSeconds) &&
                            IsValidPositionChangeColorCodingDuration(durationSeconds))
                            _positionChangeColorCodingDurationSeconds = durationSeconds;
                        continue;
                    }

                    bool value;
                    if (!bool.TryParse(settingValue, out value))
                        continue;

                    if (string.Equals(key, "ShowTeamNamesInLeaderboard", StringComparison.OrdinalIgnoreCase))
                        _showTeamNamesInsteadOfDriverNames = value;
                    else if (string.Equals(key, "AdditionalTeamNamesInRelative", StringComparison.OrdinalIgnoreCase))
                        _additionalTeamNamesInRelative = value;
                    else if (string.Equals(key, "Debugging", StringComparison.OrdinalIgnoreCase))
                        _debuggingEnabled = value;
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
                _showPositionsGained = false;
                _positionChangeColorCodingDurationSeconds = 3;
                _debuggingEnabled = false;
            }
        }

        private string _teamSpeakApiKey = "";

        private string GetTeamSpeakApiKey() { return _teamSpeakApiKey; }

        private void SetTeamSpeakApiKey(string value)
        {
            _teamSpeakApiKey = (value ?? "").Trim();
            _teamSpeak.Configure(_teamSpeakApiKey);
            SavePluginSettings();
        }

        private void LoadTeamSpeakApiKey()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                foreach (string line in File.ReadAllLines(SettingsFilePath))
                {
                    if (!line.StartsWith("TeamSpeakApiKeyProtected=", StringComparison.Ordinal)) continue;
                    byte[] encrypted = Convert.FromBase64String(line.Substring(line.IndexOf('=') + 1));
                    _teamSpeakApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
                    _teamSpeak.Configure(_teamSpeakApiKey);
                    break;
                }
            }
            catch { _teamSpeakApiKey = ""; }
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

        private void SetShowPositionsGained(bool value)
        {
            _showPositionsGained = value;
            SavePluginSettings();
        }

        private void SetPositionChangeColorCodingDuration(int value)
        {
            int durationSeconds = IsValidPositionChangeColorCodingDuration(value) ? value : 3;
            _positionChangeColorCodingDurationSeconds = durationSeconds;

            DateTime now = DateTime.UtcNow;
            foreach (PreviousPositionState state in _previousPositionByVehicleId.Values)
            {
                state.HoldUntilUtc = state.ChangeTimeUtc.AddSeconds(durationSeconds);
                if (durationSeconds == 0 || now >= state.HoldUntilUtc)
                    state.PreviousPosition = state.CurrentPosition;
            }

            SavePluginSettings();
        }

        private static bool IsValidPositionChangeColorCodingDuration(int value)
        {
            return value == 0 || value == 3 || value == 5;
        }

        private void SetShowLastPitLapAndTimeInRelative(bool value)
        {
            _showLastPitLapAndTimeInRelative = value;
            SavePluginSettings();
        }

        private void SetDebuggingEnabled(bool value)
        {
            _debuggingEnabled = value;
            PluginPerformanceDiagnostics.SetEnabled(value);
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
                    "LeaderboardShowPositionsGained=" + _showPositionsGained,
                    "LeaderboardPositionChangeColorCodingSeconds=" + _positionChangeColorCodingDurationSeconds,
                    "RelativeShowLastPitLapAndTimeDuringRace=" + _showLastPitLapAndTimeInRelative,
                    "Debugging=" + _debuggingEnabled,
                    "TeamSpeakApiKeyProtected=" + (string.IsNullOrEmpty(_teamSpeakApiKey) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(_teamSpeakApiKey), null, DataProtectionScope.CurrentUser)))
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
                "MMO_SettingsLeaderboardShowPositionsGained",
                "MMO_SettingsLeaderboardPositionChangeColorCoding",
                "MMO_SettingsRelativeShowLastPitLapAndTimeDuringRace",
                "MMO_Teamspeak_Connected", "MMO_Teamspeak_Status", "MMO_Teamspeak_ServerName",
                "MMO_Teamspeak_ChannelName", "MMO_Teamspeak_PlayerName", "MMO_Teamspeak_PlayerStatus",
                "MMO_Teamspeak_PlayerTalking", "MMO_Teamspeak_ClientCount",
                "MMO_InvalidLap",
                "MMO_PlayerInc",
                "MMO_PlayerIncidents",
                "MMO_IncidentsLimit",
                "MMO_maxIncidents",
                "MMO_RelativeAhead1",
                "MMO_RelativeAhead2",
                "MMO_RelativeAhead3",
                "MMO_RelativeBehind1",
                "MMO_RelativeBehind2",
                "MMO_RelativeBehind3"
            };

            for (int i = 1; i <= 15; i++)
            {
                names.Add("MMO_Teamspeak_" + i + "_Name");
                names.Add("MMO_Teamspeak_" + i + "_Status");
                names.Add("MMO_Teamspeak_" + i + "_Talking");
            }

            string[] playerSuffixes =
            {
                "Overall", "Class", "CarNumber", "CarClass",
                "TyreCompFL", "TyreCompFR", "TyreCompRL", "TyreCompRR",
                "DriverName", "DriveNameRelative", "TeamName",
                "BestLaptime", "LastLaptime", "GapToLeader",
                "IntervalToNext", "PitStatus", "PitLap", "PitTime", "StintLap", "VirtualEnergy",
                "VELap", "FuelLap", "FuelTarget", "VEAvg3Laps", "VELapsLeft", "FuelLapsLeft", "RefuelVE",
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
                "prevPosition",
                "PositionGain",
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
                "StintLap",
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
            bool measurePerformance = PluginPerformanceDiagnostics.Enabled;
            long performanceStartedAt = measurePerformance ? Stopwatch.GetTimestamp() : 0L;
            long allocatedBytesAtStart = measurePerformance
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            try
            {
                lock (_dataSnapshotLock)
                    DataUpdateSnapshot(pluginManager, ref data);
            }
            finally
            {
                if (measurePerformance)
                {
                    PluginPerformanceDiagnostics.DataUpdate.Add(performanceStartedAt, allocatedBytesAtStart);
                    PluginPerformanceDiagnostics.TryLogPeriodicSummary();
                }
            }
        }

        private void DataUpdateSnapshot(
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

                _lastKnownVehicleIdByPosition[positionIndex] = vehicle.mID;
                _lastKnownVehicleSeenUtcByPosition[positionIndex] = DateTime.UtcNow;
                UpdatePreviousPosition(vehicle.mID, place);

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
                            WasInGarage = vehicle.mInGarageStall,
                            RaceGreenFlagBaselinePending =
                                _scoringInfo.mSession >= 10 &&
                                _scoringInfo.mSession <= 13 &&
                                _scoringInfo.mGamePhase <= 4,
                            PitEntryLap = vehicle.mInGarageStall
                                ? Math.Max(1, vehicle.mTotalLaps + 1)
                                : 0,
                            HasPitEntryLap = vehicle.mInGarageStall,
                            UpdatePitLapOnGarageExit = vehicle.mInGarageStall
                        };
                }
            }

            long telemetryNow = Stopwatch.GetTimestamp();
            bool refreshTelemetrySnapshot = !_hasTelemetrySnapshot ||
                telemetryNow - _lastTelemetrySnapshotTimestamp >=
                    Stopwatch.Frequency * TelemetrySnapshotIntervalMilliseconds / 1000L;
            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById = _telemetryByVehicleId;
            if (refreshTelemetrySnapshot)
            {
                bool measureTelemetry = PluginPerformanceDiagnostics.Enabled;
                long performanceStageStartedAt = measureTelemetry ? Stopwatch.GetTimestamp() : 0L;
                long allocatedBytesAtStageStart = measureTelemetry
                    ? GC.GetAllocatedBytesForCurrentThread()
                    : 0L;
                LMUVehicleTelemetryRelative playerTelemetry;
                bool hasPlayerTelemetry;
                int playerTelemetryIndex;
                bool playerHasVehicle;
                bool hasPlayerTelemetryHeader;
                telemetryById = ReadVehicleTelemetryRelativeById(
                    out playerTelemetry,
                    out hasPlayerTelemetry,
                    out playerTelemetryIndex,
                    out playerHasVehicle,
                    out hasPlayerTelemetryHeader
                );
                _playerTelemetryForTrackLimits = playerTelemetry;
                _hasPlayerTelemetryForTrackLimits = hasPlayerTelemetry;
                _playerTelemetryIndexForTrackLimits = playerTelemetryIndex;
                _playerHasVehicleForTrackLimits = playerHasVehicle;
                _hasPlayerTelemetryHeaderForTrackLimits = hasPlayerTelemetryHeader;
                _lastTelemetrySnapshotTimestamp = Stopwatch.GetTimestamp();
                _hasTelemetrySnapshot = true;
                if (measureTelemetry)
                    PluginPerformanceDiagnostics.TelemetryScan.Add(
                        performanceStageStartedAt,
                        allocatedBytesAtStageStart
                    );
            }

            bool measureLiveCalculations = PluginPerformanceDiagnostics.Enabled;
            long liveCalculationStartedAt = measureLiveCalculations ? Stopwatch.GetTimestamp() : 0L;
            long liveCalculationAllocatedBytes = measureLiveCalculations
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            if (refreshTelemetrySnapshot)
                SyncSavedVehicleModels(telemetryById);
            UpdatePositionGains();
            // Relative gaps use a fresh telemetry read every update. Bypass
            // the 50 ms snapshot cache so pit-lane timing transitions do not
            // retain stale gap samples.
            UpdateRelativeGaps(ReadRelativeVehicleTelemetryById());
            if (refreshTelemetrySnapshot)
            {
                UpdateTrackLimitPoints(
                    telemetryById,
                    _playerTelemetryForTrackLimits,
                    _hasPlayerTelemetryForTrackLimits
                );
            }
            UpdateLastLapTimes();
            UpdatePitStopTimes();
            UpdateStintLaps(telemetryById, refreshTelemetrySnapshot);
            UpdatePlayerConsumption(telemetryById);
            if (measureLiveCalculations)
                PluginPerformanceDiagnostics.LiveCalculations.Add(
                    liveCalculationStartedAt,
                    liveCalculationAllocatedBytes
                );
        }

        private void UpdatePositionGains()
        {
            if (_scoringInfo.mSession < 10 || _scoringInfo.mSession > 13)
            {
                Array.Clear(_positionGainByPosition, 0, _positionGainByPosition.Length);
                return;
            }

            string trackName = DecodeString(_scoringInfo.mTrackName).Trim();
            if (string.IsNullOrEmpty(trackName))
                return;

            double startET = _scoringInfo.mStartET;
            if (double.IsNaN(startET) || double.IsInfinity(startET))
                startET = 0.0;

            bool snapshotKeyChanged =
                _raceGridSession != _scoringInfo.mSession ||
                !string.Equals(_raceGridTrackName, trackName, StringComparison.OrdinalIgnoreCase) ||
                double.IsNaN(_raceGridStartET) ||
                Math.Abs(_raceGridStartET - startET) > 1.0;
            if (snapshotKeyChanged)
            {
                ResetRaceStartSnapshotCache();
                _raceGridSession = _scoringInfo.mSession;
                _raceGridTrackName = trackName;
                _raceGridStartET = startET;
            }

            // Keep refreshing the provisional grid through formation, then
            // freeze it as soon as the starting-light countdown begins.
            if (_scoringInfo.mGamePhase <= 4)
            {
                int gridCount = 0;
                for (int i = 0; i < MaxVehicles; i++)
                {
                    if (!_hasVehicle[i])
                        continue;

                    LMUVehicleScoring vehicle = _vehiclesByPosition[i];
                    if (vehicle.mID < 0 || vehicle.mPlace < 1 || vehicle.mPlace > MaxVehicles)
                        continue;

                    _raceGridCandidateCarIds[gridCount] = vehicle.mID;
                    _raceGridCandidatePositions[gridCount] = vehicle.mPlace;
                    gridCount++;
                }

                bool gridChanged = gridCount != _raceGridCount;
                if (!gridChanged)
                {
                    for (int i = 0; i < gridCount; i++)
                    {
                        if (_raceGridCarIds[i] != _raceGridCandidateCarIds[i] ||
                            _raceGridPositions[i] != _raceGridCandidatePositions[i])
                        {
                            gridChanged = true;
                            break;
                        }
                    }
                }

                bool freeze = _scoringInfo.mGamePhase >= 4;
                bool freezeChanged = freeze && !_raceGridFrozen;
                if (gridChanged)
                {
                    Array.Copy(_raceGridCandidateCarIds, _raceGridCarIds, gridCount);
                    Array.Copy(_raceGridCandidatePositions, _raceGridPositions, gridCount);
                    for (int i = gridCount; i < _raceGridCount; i++)
                    {
                        _raceGridCarIds[i] = -1;
                        _raceGridPositions[i] = 0;
                    }
                    _raceGridCount = gridCount;
                }

                if (gridChanged || freezeChanged)
                {
                    _raceGridSaveBuffer.Clear();
                    for (int i = 0; i < _raceGridCount; i++)
                    {
                        _raceGridSaveBuffer.Add(new RaceStartPosition
                        {
                            CarId = _raceGridCarIds[i],
                            GridPosition = _raceGridPositions[i]
                        });
                    }

                    try
                    {
                        _statisticsStore.SaveRaceStartSnapshot(
                            trackName,
                            _scoringInfo.mSession,
                            startET,
                            _raceGridSaveBuffer,
                            freeze
                        );
                        RaceStartPositionSnapshot snapshot = _statisticsStore.GetRaceStartSnapshot(
                            trackName,
                            _scoringInfo.mSession,
                            startET
                        );
                        _raceStartSnapshotLoaded = true;
                        CacheRaceStartPositions(snapshot);
                    }
                    catch { /* Keep the live overlay working if the snapshot cannot be saved. */ }
                }

                _raceGridFrozen = freeze;
            }

            if (!_raceStartSnapshotLoaded)
            {
                try
                {
                    RaceStartPositionSnapshot snapshot = _statisticsStore.GetRaceStartSnapshot(
                        trackName,
                        _scoringInfo.mSession,
                        startET
                    );
                    _raceStartSnapshotLoaded = true;
                    CacheRaceStartPositions(snapshot);
                }
                catch { return; }
            }

            for (int i = 0; i < MaxVehicles; i++)
            {
                if (!_hasVehicle[i])
                    continue;

                int startPosition;
                LMUVehicleScoring vehicle = _vehiclesByPosition[i];
                if (_raceStartPositionByCarId.TryGetValue(vehicle.mID, out startPosition))
                    _positionGainByPosition[i] = startPosition - vehicle.mPlace;
                else
                    _positionGainByPosition[i] = 0;
            }
        }

        private void CacheRaceStartPositions(RaceStartPositionSnapshot snapshot)
        {
            _raceStartPositionByCarId.Clear();
            if (snapshot == null || snapshot.Vehicles == null)
                return;

            foreach (RaceStartPosition vehicle in snapshot.Vehicles)
            {
                if (vehicle != null && vehicle.CarId >= 0 && vehicle.GridPosition > 0)
                    _raceStartPositionByCarId[vehicle.CarId] = vehicle.GridPosition;
            }
        }

        private void ResetRaceStartSnapshotCache()
        {
            _raceGridCount = 0;
            _raceGridFrozen = false;
            _raceStartSnapshotLoaded = false;
            _raceStartPositionByCarId.Clear();
            Array.Clear(_raceGridPositions, 0, _raceGridPositions.Length);
            Array.Clear(_raceGridCandidatePositions, 0, _raceGridCandidatePositions.Length);
            for (int i = 0; i < MaxVehicles; i++)
            {
                _raceGridCarIds[i] = -1;
                _raceGridCandidateCarIds[i] = -1;
            }
        }

        private void SyncSavedVehicleModels(
            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById)
        {
            if (telemetryById == null || telemetryById.Count == 0)
                return;

            for (int positionIndex = 0; positionIndex < MaxVehicles; positionIndex++)
            {
                if (!_hasVehicle[positionIndex])
                    continue;

                LMUVehicleScoring scoring = _vehiclesByPosition[positionIndex];
                LMUVehicleTelemetryRelative telemetry = new LMUVehicleTelemetryRelative();
                if (!telemetryById.TryGetValue(scoring.mID, out telemetry))
                    continue;

                string vehicleModel = DecodeString(telemetry.mVehicleModel).Trim();
                if (string.IsNullOrWhiteSpace(vehicleModel))
                    continue;

                string vehicleFile = DecodeString(scoring.mVehFilename).Trim();
                string vehicleName = DecodeString(scoring.mVehicleName).Trim();
                string syncIdentity = scoring.mID.ToString(CultureInfo.InvariantCulture) + "|" +
                    vehicleFile + "|" + vehicleName + "|" + vehicleModel;
                if (!_syncedVehicleModelIdentities.Add(syncIdentity))
                    continue;

                try
                {
                    _statisticsStore.UpdateVehicleModelName(
                        vehicleFile,
                        vehicleName,
                        GetStatisticsCarModelName(vehicleFile, vehicleName),
                        vehicleModel);
                }
                catch
                {
                    // Updating existing display names must not interrupt live telemetry.
                }
            }
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
            ResetRaceStartSnapshotCache();
            _raceGridTrackName = "";
            _raceGridSession = -1;
            _raceGridStartET = double.NaN;
            _hasTelemetrySnapshot = false;
            _telemetryByVehicleId.Clear();
            _pitStopTimingByVehicleId.Clear();
            _stintLapByVehicleId.Clear();
            _lastLapTimingByVehicleId.Clear();
            _tyreStateByVehicleId.Clear();
            ClearRelativeGaps();
            ClearTrackLimitPoints();
            _previousPositionByVehicleId.Clear();
            ResetPlayerConsumptionForSession();
            _lastPlayerVehicleId = -1;
            for (int i = 0; i < MaxVehicles; i++)
            {
                _lastKnownVehicleIdByPosition[i] = -1;
                _lastKnownVehicleSeenUtcByPosition[i] = DateTime.MinValue;
                _positionGainByPosition[i] = 0;
            }
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
            _playerConsumption.CachedVehicleFileBytes = null;
            _playerConsumption.CachedVehicleNameBytes = null;
            _playerConsumption.CachedVehicleModelBytes = null;
            _playerConsumption.CachedTrackNameBytes = null;
            _playerConsumption.CachedVehicleFile = "";
            _playerConsumption.CachedVehicleName = "";
            _playerConsumption.CachedVehicleModel = "";
            _playerConsumption.CachedTrackName = "";
            _playerConsumption.CachedCarModelName = "";
            _playerConsumption.CachedApiCarClass = "";
            _playerConsumption.CachedApiCarNumber = "";
            _playerConsumption.CachedApiTeamName = "";
            _playerConsumption.CachedVehicleMetadataVersion = -1;
            _playerConsumption.VehicleMetadataChecked = false;
            _playerConsumption.HasVehicleMetadata = false;
            _playerConsumption.CachedManufacturer = "";
            _playerConsumption.CachedMetadataModel = "";
            _playerConsumption.StatisticsKey = "";
            _playerConsumption.StatisticsCarKey = "";
            _playerConsumption.StatisticsCarName = "";
            _playerConsumption.StatisticsManufacturer = "";
            _playerConsumption.StatisticsModel = "";
            _playerConsumption.StatisticsTrackName = "";
            _playerConsumption.FuelTarget = 0.0;
            _playerConsumption.HasFuelTarget = false;
            _playerConsumption.HasVirtualEnergy = false;
            _playerConsumption.HasVirtualEnergyStored = false;
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

        private void UpdatePlayerConsumption(
            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById)
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

            LMUVehicleTelemetryRelative playerTelemetry = new LMUVehicleTelemetryRelative();
            bool hasPlayerTelemetry = telemetryById != null &&
                telemetryById.TryGetValue(scoring.mID, out playerTelemetry);
            byte[] vehicleModelBytes = hasPlayerTelemetry ? playerTelemetry.mVehicleModel : null;

            bool vehicleFileChanged = !ByteArraysEqual(
                scoring.mVehFilename,
                _playerConsumption.CachedVehicleFileBytes);
            bool vehicleNameChanged = !ByteArraysEqual(
                scoring.mVehicleName,
                _playerConsumption.CachedVehicleNameBytes);
            bool vehicleModelChanged = !ByteArraysEqual(
                vehicleModelBytes,
                _playerConsumption.CachedVehicleModelBytes);
            bool trackChanged = !ByteArraysEqual(
                _scoringInfo.mTrackName,
                _playerConsumption.CachedTrackNameBytes);
            bool apiClassChanged = !string.Equals(
                apiVehicle.CarClass ?? "",
                _playerConsumption.CachedApiCarClass,
                StringComparison.Ordinal);
            bool apiNumberChanged = !string.Equals(
                apiVehicle.CarNumber ?? "",
                _playerConsumption.CachedApiCarNumber,
                StringComparison.Ordinal);
            bool apiTeamChanged = !string.Equals(
                apiVehicle.TeamName ?? "",
                _playerConsumption.CachedApiTeamName,
                StringComparison.Ordinal);

            string vehicleFile = vehicleFileChanged
                ? DecodeString(scoring.mVehFilename).Trim()
                : _playerConsumption.CachedVehicleFile;
            string vehicleName = vehicleNameChanged
                ? DecodeString(scoring.mVehicleName).Trim()
                : _playerConsumption.CachedVehicleName;
            string vehicleModel = vehicleModelChanged
                ? DecodeString(vehicleModelBytes).Trim()
                : _playerConsumption.CachedVehicleModel;
            string trackName = trackChanged
                ? DecodeString(_scoringInfo.mTrackName).Trim()
                : _playerConsumption.CachedTrackName;

            bool vehicleIdChanged = _playerConsumption.VehicleId != scoring.mID;
            bool identityChanged = vehicleIdChanged || vehicleFileChanged || vehicleNameChanged ||
                apiClassChanged || apiNumberChanged;
            string vehicleIdentity = identityChanged
                ? GetConsumptionVehicleIdentity(vehicleFile, vehicleName, apiVehicle)
                : _playerConsumption.VehicleIdentity;
            if (vehicleIdChanged)
            {
                bool sameCar = !string.IsNullOrEmpty(_playerConsumption.VehicleIdentity) &&
                    string.Equals(_playerConsumption.VehicleIdentity, vehicleIdentity,
                        StringComparison.OrdinalIgnoreCase);
                if (sameCar)
                    ResetPlayerConsumptionForSession();
                else
                    ResetPlayerConsumption();
                _playerConsumption.VehicleId = scoring.mID;
            }

            CacheChangedBytes(scoring.mVehFilename, ref _playerConsumption.CachedVehicleFileBytes);
            CacheChangedBytes(scoring.mVehicleName, ref _playerConsumption.CachedVehicleNameBytes);
            CacheChangedBytes(vehicleModelBytes, ref _playerConsumption.CachedVehicleModelBytes);
            CacheChangedBytes(_scoringInfo.mTrackName, ref _playerConsumption.CachedTrackNameBytes);
            _playerConsumption.CachedVehicleFile = vehicleFile;
            _playerConsumption.CachedVehicleName = vehicleName;
            _playerConsumption.CachedVehicleModel = vehicleModel;
            _playerConsumption.CachedTrackName = trackName;
            _playerConsumption.CachedApiCarClass = apiVehicle.CarClass ?? "";
            _playerConsumption.CachedApiCarNumber = apiVehicle.CarNumber ?? "";
            _playerConsumption.CachedApiTeamName = apiVehicle.TeamName ?? "";
            if (!string.IsNullOrEmpty(vehicleIdentity))
                _playerConsumption.VehicleIdentity = vehicleIdentity;

            bool carModelChanged = vehicleFileChanged || vehicleNameChanged ||
                string.IsNullOrEmpty(_playerConsumption.CachedCarModelName);
            string carModelName = carModelChanged
                ? GetStatisticsCarModelName(vehicleFile, vehicleName)
                : _playerConsumption.CachedCarModelName;
            _playerConsumption.CachedCarModelName = carModelName;
            string carKey = carModelName;

            int metadataVersion = _standingsApi.VehicleMetadataVersion;
            bool metadataLookupNeeded = !_playerConsumption.VehicleMetadataChecked ||
                _playerConsumption.CachedVehicleMetadataVersion != metadataVersion ||
                vehicleFileChanged || vehicleNameChanged || apiClassChanged ||
                apiNumberChanged || apiTeamChanged;
            bool newlyResolvedMetadata = false;
            if (metadataLookupNeeded)
            {
                string manufacturer;
                string model;
                _playerConsumption.HasVehicleMetadata = _standingsApi.TryGetVehicleMetadata(
                    vehicleFile, vehicleName, apiVehicle.TeamName, apiVehicle.CarNumber,
                    out manufacturer, out model);
                _playerConsumption.VehicleMetadataChecked = true;
                _playerConsumption.CachedVehicleMetadataVersion = metadataVersion;
                _playerConsumption.CachedManufacturer = _playerConsumption.HasVehicleMetadata
                    ? manufacturer
                    : "";
                _playerConsumption.CachedMetadataModel = _playerConsumption.HasVehicleMetadata
                    ? model
                    : "";
                newlyResolvedMetadata = _playerConsumption.HasVehicleMetadata;
            }

            bool apiReportsVirtualEnergy = IsFinite(apiVehicle.VeFraction) &&
                apiVehicle.VeFraction > 0.0;
            if (apiReportsVirtualEnergy)
                _playerConsumption.HasVirtualEnergy = true;

            bool displayNameChanged = vehicleIdChanged || carModelChanged || vehicleModelChanged ||
                newlyResolvedMetadata || string.IsNullOrEmpty(_playerConsumption.StatisticsCarName);
            string carName = _playerConsumption.StatisticsCarName;
            if (displayNameChanged)
            {
                carName = string.IsNullOrWhiteSpace(vehicleModel)
                    ? carModelName
                    : vehicleModel;
                if (!string.IsNullOrWhiteSpace(vehicleModel))
                {
                    _playerConsumption.StatisticsManufacturer = "";
                    _playerConsumption.StatisticsModel = vehicleModel;
                }
                if (_playerConsumption.HasVehicleMetadata)
                {
                    _playerConsumption.StatisticsManufacturer = _playerConsumption.CachedManufacturer;
                    if (string.IsNullOrWhiteSpace(vehicleModel))
                        _playerConsumption.StatisticsModel = _playerConsumption.CachedMetadataModel;
                    carName = FormatStatisticsVehicleName(
                        _playerConsumption.CachedManufacturer,
                        string.IsNullOrWhiteSpace(vehicleModel)
                            ? _playerConsumption.CachedMetadataModel
                            : vehicleModel);
                }
                _playerConsumption.StatisticsCarName = carName;
            }

            string statisticsKey = _playerConsumption.StatisticsKey;
            if (string.IsNullOrEmpty(statisticsKey) ||
                !string.Equals(carKey, _playerConsumption.StatisticsCarKey, StringComparison.Ordinal) ||
                !string.Equals(trackName, _playerConsumption.StatisticsTrackName, StringComparison.Ordinal))
            {
                statisticsKey = carKey + "\n" + trackName;
            }

            if (_playerConsumption.HasVehicleMetadata &&
                (newlyResolvedMetadata || vehicleModelChanged || trackChanged ||
                 !string.Equals(statisticsKey, _playerConsumption.StatisticsKey, StringComparison.Ordinal)))
            {
                try
                {
                    _statisticsStore.UpdateVehicleMetadata(
                        carKey, vehicleFile, trackName,
                        _playerConsumption.CachedManufacturer,
                        string.IsNullOrWhiteSpace(vehicleModel)
                            ? _playerConsumption.CachedMetadataModel
                            : vehicleModel,
                        carName);
                }
                catch { /* Metadata is an optional display enhancement. */ }
            }
            if (!string.Equals(
                    _playerConsumption.StatisticsKey,
                    statisticsKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                _playerConsumption.StatisticsKey = statisticsKey;
                _playerConsumption.StatisticsCarKey = carKey;
                _playerConsumption.StatisticsCarName = carName;
                _playerConsumption.StatisticsTrackName = trackName;
                _playerConsumption.LastFuelPerLap = 0.0;
                _playerConsumption.LastVePerLap = 0.0;
                _playerConsumption.FuelTarget = 0.0;
                _playerConsumption.HasFuelTarget = false;
                _playerConsumption.HasVirtualEnergy = apiReportsVirtualEnergy;
                _playerConsumption.HasVirtualEnergyStored = false;
                _playerConsumption.FuelLapHistory.Clear();
                _playerConsumption.VeLapHistory.Clear();

                CarTrackStatisticsRecord saved = null;
                string legacyVehicleFile = vehicleFile;
                try { saved = _statisticsStore.GetRecord(carKey, trackName, legacyVehicleFile); }
                catch { /* Stored rates are optional; continue with live telemetry if unavailable. */ }
                if (saved != null)
                {
                    _playerConsumption.LastFuelPerLap = saved.FuelFallbackAverage;
                    _playerConsumption.LastVePerLap = saved.LastFiveVeAverage;
                    _playerConsumption.HasVirtualEnergy = _playerConsumption.HasVirtualEnergy ||
                        saved.HasVirtualEnergy || saved.LastFiveVeAverage > 0.0;
                    _playerConsumption.HasVirtualEnergyStored = saved.HasVirtualEnergy;
                    bool targetUsesCurrentUnits = saved.FuelTargetUsesVirtualEnergy ==
                        _playerConsumption.HasVirtualEnergy;
                    _playerConsumption.FuelTarget = saved.HasFuelTarget && targetUsesCurrentUnits
                        ? saved.FuelTarget
                        : 0.0;
                    _playerConsumption.HasFuelTarget = saved.HasFuelTarget && targetUsesCurrentUnits;
                    AddRecentSamples(_playerConsumption.FuelLapHistory, saved.LastFuelLaps);
                    AddRecentSamples(_playerConsumption.VeLapHistory, saved.LastVeLaps);
                }
            }

            if (apiReportsVirtualEnergy)
                _playerConsumption.HasVirtualEnergy = true;
            if (_playerConsumption.HasVirtualEnergy && !_playerConsumption.HasVirtualEnergyStored)
            {
                try
                {
                    _statisticsStore.MarkVehicleHasVirtualEnergy(carKey, carName, trackName);
                    _playerConsumption.HasVirtualEnergyStored = true;
                }
                catch { /* Stored car capability is optional; keep telemetry updating. */ }
            }

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

                bool canMeasureConsumption =
                    _playerConsumption.HasInitialSectorLapConsumption &&
                    !_playerConsumption.AwaitingStartFinishBaseline;
                double completedFuelUsed = _playerConsumption.FuelUsedThisLap;
                double completedVeUsed = _playerConsumption.VeUsedThisLap;
                bool lapHadPitLane =
                    _playerConsumption.LapIncludedPitLane ||
                    scoring.mInPits || scoring.mInGarageStall;
                if (_playerConsumption.AwaitingStartFinishBaseline)
                {
                    // The initial sector-to-line outlap measurement is only
                    // a baseline. Start collecting full laps from this line.
                    _playerConsumption.AwaitingStartFinishBaseline = false;
                    ResetConsumptionWindow(hasFuel, fuelLevel, hasVe, veLevel);
                }
                else if (_playerConsumption.HasInitialSectorLapConsumption)
                {
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

                try
                {
                    _statisticsStore.RecordCompletedLap(
                        carKey,
                        carName,
                        trackName,
                        lastLapTime,
                        canMeasureConsumption && !lapHadPitLane && hasFuel,
                        completedFuelUsed,
                        canMeasureConsumption && !lapHadPitLane && hasVe,
                        completedVeUsed,
                        _playerConsumption.StatisticsManufacturer,
                        _playerConsumption.StatisticsModel,
                        vehicleModel
                    );
                }
                catch { /* Keep telemetry updates running if persistence is temporarily unavailable. */ }
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

        private void SyncSavedVehicleMetadata()
        {
            bool measurePerformance = PluginPerformanceDiagnostics.Enabled;
            long performanceStartedAt = measurePerformance ? Stopwatch.GetTimestamp() : 0L;
            long allocatedBytesAtStart = measurePerformance
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            try
            {
                foreach (CarTrackStatisticsRecord record in _statisticsStore.GetRecords())
                {
                    if (record == null) continue;
                    string vehicleFile = (record.CarKey ?? "").Split('|')[0].Trim();
                    string manufacturer;
                    string model;
                    if (!_standingsApi.TryGetVehicleMetadata(
                            vehicleFile, record.CarName, "", "", out manufacturer, out model))
                        continue;

                    _statisticsStore.UpdateVehicleMetadata(
                        record.CarKey,
                        vehicleFile,
                        record.TrackKey,
                        manufacturer,
                        model,
                        manufacturer + " " + model);
                }
            }
            catch
            {
                // Updating display names must not interrupt live telemetry.
            }
            finally
            {
                if (measurePerformance)
                    PluginPerformanceDiagnostics.VehicleMetadataSync.Add(performanceStartedAt, allocatedBytesAtStart);
            }
        }

        private void AdjustFuelTarget(double adjustment)
        {
            string carKey = _playerConsumption.StatisticsCarKey;
            string carName = _playerConsumption.StatisticsCarName;
            string trackName = _playerConsumption.StatisticsTrackName;
            if (string.IsNullOrWhiteSpace(carKey) || string.IsNullOrWhiteSpace(trackName))
                return;

            double current = _playerConsumption.FuelTarget;
            if (!_playerConsumption.HasFuelTarget)
            {
                double baseline = _playerConsumption.HasVirtualEnergy
                    ? _playerConsumption.LastVePerLap
                    : _playerConsumption.LastFuelPerLap;
                current = IsFinite(baseline) && baseline > 0.0 ? baseline : 0.0;
            }

            double updated = Math.Max(0.0, Math.Round(current + adjustment, 2));
            try
            {
                _statisticsStore.SetFuelTarget(
                    carKey,
                    carName,
                    trackName,
                    updated,
                    _playerConsumption.HasVirtualEnergy
                );
                _playerConsumption.FuelTarget = updated;
                _playerConsumption.HasFuelTarget = true;
                if (_playerConsumption.HasVirtualEnergy)
                    _playerConsumption.HasVirtualEnergyStored = true;
            }
            catch
            {
                // A storage failure should not interrupt SimHub input processing.
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
            string vehicleFile,
            string vehicleName,
            LMUApiVehicle apiVehicle)
        {
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

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private static void CacheChangedBytes(byte[] source, ref byte[] cached)
        {
            if (ByteArraysEqual(source, cached))
                return;
            cached = source == null ? null : (byte[])source.Clone();
        }

        private static string GetStatisticsCarModelName(string vehicleFile, string vehicleName)
        {
            string model = (vehicleFile ?? "").Trim();
            int separator = Math.Max(model.LastIndexOf('/'), model.LastIndexOf('\\'));
            if (separator >= 0 && separator + 1 < model.Length)
                model = model.Substring(separator + 1);
            int extension = model.LastIndexOf('.');
            if (extension > 0) model = model.Substring(0, extension);

            // LMU's 499P vehicle file identifies the model more reliably than
            // the API's team entry, which can contain a custom team name.
            if (model.IndexOf("499P", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Ferrari 499P";

            model = Regex.Replace(model, @"^\d+[_-]\d+[_-]", "");
            model = Regex.Replace(model, "[_-]+", " ").Trim();
            if (model.Length > 0)
                return model;

            // Keep a useful fallback when shared memory does not provide a
            // vehicle filename; this name can be team-specific in LMU.
            return string.IsNullOrWhiteSpace(vehicleName) ? "Unknown car" : vehicleName.Trim();
        }

        internal static string FormatStatisticsVehicleName(
            string manufacturer,
            string vehicleModel)
        {
            string normalizedModel = Regex.Replace(
                (vehicleModel ?? "").Trim(),
                "[_-]+",
                " ");
            normalizedModel = Regex.Replace(normalizedModel, @"\s+", " ").Trim();
            string normalizedManufacturer = (manufacturer ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalizedManufacturer))
                return normalizedModel;
            if (normalizedModel.StartsWith(
                    normalizedManufacturer + " ",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedModel, normalizedManufacturer, StringComparison.OrdinalIgnoreCase))
                return normalizedModel;
            return string.IsNullOrWhiteSpace(normalizedModel)
                ? normalizedManufacturer
                : normalizedManufacturer + " " + normalizedModel;
        }

        private static void AddRecentSamples(List<double> destination, List<double> samples)
        {
            if (destination == null || samples == null) return;
            int start = Math.Max(0, samples.Count - 3);
            for (int i = start; i < samples.Count; i++)
            {
                double value = samples[i];
                if (IsFinite(value) && value > 0.0)
                    destination.Add(value);
            }
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

            const double refuelBufferLaps = 0.3;
            return (lapsRemaining + refuelBufferLaps) * averageVePerLap - currentVe;
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
                    state = new PitStopTimingState
                    {
                        RaceGreenFlagBaselinePending =
                            _scoringInfo.mSession >= 10 &&
                            _scoringInfo.mSession <= 13 &&
                            _scoringInfo.mGamePhase <= 4
                    };
                    _pitStopTimingByVehicleId.Add(vehicle.mID, state);
                }

                bool inPitLane = vehicle.mInPits;
                bool isRaceSession =
                    _scoringInfo.mSession >= 10 && _scoringInfo.mSession <= 13;
                bool raceHasStarted = isRaceSession && _scoringInfo.mGamePhase > 4;
                if (inPitLane && !state.WasInPitLane)
                {
                    // Save the lap at pit entry; commit it only when this visit
                    // becomes a stop or a recognized race start from the pits.
                    state.PitEntryLap = Math.Max(1, vehicle.mTotalLaps + 1);
                    state.HasPitEntryLap = true;
                }

                bool inGarage = vehicle.mInGarageStall;
                bool leavingGarage = state.WasInGarage && !inGarage;
                if (inGarage)
                {
                    if (!state.WasInGarage)
                    {
                        bool pitLapAlreadyCommitted = state.HasStoppedThisPitVisit;
                        if (!state.HasPitEntryLap && !pitLapAlreadyCommitted)
                        {
                            state.PitEntryLap = Math.Max(1, vehicle.mTotalLaps + 1);
                            state.HasPitEntryLap = true;
                        }
                        state.UpdatePitLapOnGarageExit =
                            !pitLapAlreadyCommitted && state.HasPitEntryLap;
                    }

                    state.IsMeasuring = false;
                    state.WasInGarage = true;
                    state.HasStoppedThisPitVisit = false;
                    state.ResetOnNextStop = false;
                    state.AccumulatedPitTimeSeconds = 0.0;
                    state.LastPitTimeSeconds = 0.0;
                    state.WasInPitLane = inPitLane;
                    continue;
                }

                if (leavingGarage)
                {
                    if (inPitLane)
                    {
                        if (state.UpdatePitLapOnGarageExit && !state.HasPitEntryLap)
                        {
                            state.PitEntryLap = Math.Max(1, vehicle.mTotalLaps + 1);
                            state.HasPitEntryLap = true;
                        }
                        state.GarageExitPending = true;
                    }
                    else
                    {
                        state.HasPitEntryLap = false;
                        state.UpdatePitLapOnGarageExit = false;
                        state.GarageExitPending = false;
                    }
                    state.LastPitTimeSeconds = 0.0;
                    state.AccumulatedPitTimeSeconds = 0.0;
                    state.HasStoppedThisPitVisit = false;
                    state.ResetOnNextStop = false;
                }
                state.WasInGarage = false;

                if (raceHasStarted && state.RaceGreenFlagBaselinePending)
                {
                    if (inPitLane)
                    {
                        state.RacePitStartPending = true;
                        if (!state.HasPitEntryLap)
                        {
                            state.PitEntryLap = Math.Max(1, vehicle.mTotalLaps + 1);
                            state.HasPitEntryLap = true;
                        }
                    }
                    else if (!inGarage)
                    {
                        state.RaceGreenFlagBaselinePending = false;
                    }
                }

                double speed = Math.Sqrt(
                    vehicle.mLocalVel.x * vehicle.mLocalVel.x +
                    vehicle.mLocalVel.y * vehicle.mLocalVel.y +
                    vehicle.mLocalVel.z * vehicle.mLocalVel.z
                );
                bool stationaryInPit = inPitLane &&
                    !double.IsNaN(speed) && !double.IsInfinity(speed) && speed < 0.1;

                if (stationaryInPit && !state.IsMeasuring)
                {
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

                bool exitedPitLane = state.WasInPitLane && !inPitLane;
                bool completedTimedPitStop =
                    exitedPitLane && state.HasStoppedThisPitVisit;
                if (exitedPitLane &&
                    (completedTimedPitStop || state.GarageExitPending ||
                     state.RacePitStartPending))
                {
                    CommitPitLapAtPitExit(state);
                }
                if (completedTimedPitStop)
                    state.ResetOnNextStop = true;

                state.WasInPitLane = inPitLane;
            }
        }

        private void CommitPitLapAtPitExit(PitStopTimingState state)
        {
            state.HasStoppedThisPitVisit = false;
            state.GarageExitPending = false;
            state.RacePitStartPending = false;
            if (_scoringInfo.mGamePhase > 4)
                state.RaceGreenFlagBaselinePending = false;

            if (state.HasPitEntryLap)
            {
                state.PitLap = state.PitEntryLap;
                state.HasPitEntryLap = false;
            }
            state.UpdatePitLapOnGarageExit = false;
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

        private int GetStintLap(int position)
        {
            return GetStintLapByVehicleId(GetVehicleIdAtPosition(position));
        }

        private int GetStintLapByVehicleId(int vehicleId)
        {
            StintLapState state;
            return vehicleId >= 0 &&
                _stintLapByVehicleId.TryGetValue(vehicleId, out state) &&
                state.Active
                    ? state.StintLap
                    : 0;
        }

        private void UpdateStintLaps(
            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById,
            bool telemetrySnapshotRefreshed)
        {
            bool isRaceSession =
                _scoringInfo.mSession >= 10 && _scoringInfo.mSession <= 13;
            bool raceHasStarted = isRaceSession && _scoringInfo.mGamePhase > 4;

            for (int i = 0; i < MaxVehicles; i++)
            {
                if (!_hasVehicle[i])
                    continue;

                LMUVehicleScoring vehicle = _vehiclesByPosition[i];
                if (vehicle.mID < 0)
                    continue;

                LMUVehicleTelemetryRelative telemetry = new LMUVehicleTelemetryRelative();
                bool hasTelemetry = telemetryById != null &&
                    telemetryById.TryGetValue(vehicle.mID, out telemetry);
                if (!hasTelemetry)
                    telemetry = new LMUVehicleTelemetryRelative();

                int lapCounter = Math.Max(0, (int)vehicle.mTotalLaps);
                if (hasTelemetry && telemetry.mLapNumber >= 0)
                    lapCounter = Math.Max(lapCounter, telemetry.mLapNumber);

                StintLapState state;
                if (!_stintLapByVehicleId.TryGetValue(vehicle.mID, out state))
                {
                    state = new StintLapState();
                    _stintLapByVehicleId.Add(vehicle.mID, state);
                }

                bool inPitLane = vehicle.mInPits;
                bool inGarage = vehicle.mInGarageStall;
                bool lapBaselineResetThisTick = false;

                if (!state.Initialized)
                {
                    state.Initialized = true;
                    state.WasInPitLane = inPitLane;
                    state.WasInGarage = inGarage;
                    state.LastCompletedLap = lapCounter;
                    state.WaitingForRaceStart = isRaceSession && !raceHasStarted;
                    state.Active = !state.WaitingForRaceStart && !inGarage;
                    state.StintLap = state.Active ? 1 : 0;
                    if (inPitLane && !inGarage && hasTelemetry)
                        CaptureStintPitBaseline(state, hasTelemetry ? telemetry : (LMUVehicleTelemetryRelative?)null);
                }

                bool leavingGarage = state.WasInGarage && !inGarage;
                if (inGarage)
                {
                    // Parking in the garage closes the current outing. The next
                    // departure starts a fresh stint in practice/qualifying too.
                    state.Active = false;
                    state.StintLap = 0;
                    state.ServiceDetected = false;
                    state.HasPitBaseline = false;
                }
                else if (leavingGarage)
                {
                    state.Active = true;
                    state.WaitingForRaceStart = isRaceSession && !raceHasStarted;
                    state.StintLap = 1;
                    state.LastCompletedLap = lapCounter;
                    state.ServiceDetected = false;
                    state.HasPitBaseline = false;
                    lapBaselineResetThisTick = true;
                }

                if (state.WaitingForRaceStart && raceHasStarted && !inGarage)
                {
                    // Freeze the counter at the green-flag boundary. If LMU has
                    // already advanced its lap field on that same update, that
                    // update becomes the baseline rather than counting Lap 2.
                    state.WaitingForRaceStart = false;
                    state.Active = true;
                    state.StintLap = 1;
                    state.LastCompletedLap = lapCounter;
                    lapBaselineResetThisTick = true;
                }

                if (!inGarage && inPitLane && !state.WasInPitLane)
                {
                    state.HasPitBaseline = false;
                    if (hasTelemetry)
                        CaptureStintPitBaseline(state, telemetry);
                }

                if (!inGarage && inPitLane && !state.HasPitBaseline &&
                    hasTelemetry && telemetrySnapshotRefreshed)
                    CaptureStintPitBaseline(state, telemetry);

                if (!inGarage && inPitLane && state.HasPitBaseline && hasTelemetry &&
                    telemetrySnapshotRefreshed)
                    DetectStintService(state, telemetry);

                bool exitedPitLane = state.WasInPitLane && !inPitLane;
                if (exitedPitLane)
                {
                    if (state.ServiceDetected)
                    {
                        state.Active = true;
                        state.StintLap = 1;
                        state.LastCompletedLap = lapCounter;
                        lapBaselineResetThisTick = true;
                    }
                    state.ServiceDetected = false;
                    state.HasPitBaseline = false;
                }

                if (state.Active && !state.WaitingForRaceStart &&
                    !state.ServiceDetected &&
                    !lapBaselineResetThisTick)
                {
                    int completedLapDelta = lapCounter - state.LastCompletedLap;
                    if (completedLapDelta > 0 && completedLapDelta <= 100)
                        state.StintLap += completedLapDelta;
                    // Ignore backward jumps and implausible jumps (session or
                    // shared-memory reset); re-anchor without changing output.
                }

                state.LastCompletedLap = lapCounter;
                state.WasInPitLane = inPitLane;
                state.WasInGarage = inGarage;
            }
        }

        private static void CaptureStintPitBaseline(
            StintLapState state,
            LMUVehicleTelemetryRelative? telemetry)
        {
            state.HasPitBaseline = true;
            state.ServiceDetected = false;
            state.FuelServiceEvidenceSamples = 0;
            state.TyreServiceEvidenceSamples = 0;
            state.CompoundServiceEvidenceSamples = 0;
            state.PitEntryFuel = telemetry.HasValue &&
                IsFinite(telemetry.Value.mFuel) && telemetry.Value.mFuel >= 0.0
                    ? telemetry.Value.mFuel
                    : double.NaN;
            state.PitEntryWear = telemetry.HasValue
                ? GetAverageTyreWear(telemetry.Value)
                : double.NaN;
            state.PitEntryCompounds = telemetry.HasValue
                ? new[] {
                    telemetry.Value.mTyreCompFL,
                    telemetry.Value.mTyreCompFR,
                    telemetry.Value.mTyreCompRL,
                    telemetry.Value.mTyreCompRR
                }
                : null;
        }

        private static void DetectStintService(
            StintLapState state,
            LMUVehicleTelemetryRelative telemetry)
        {
            if (state.ServiceDetected)
                return;

            bool fuelIncreased =
                IsFinite(state.PitEntryFuel) &&
                IsFinite(telemetry.mFuel) &&
                telemetry.mFuel - state.PitEntryFuel >
                    Math.Max(0.1, state.PitEntryFuel * 0.0005);
            state.FuelServiceEvidenceSamples = fuelIncreased
                ? state.FuelServiceEvidenceSamples + 1
                : 0;
            if (state.FuelServiceEvidenceSamples >= 2)
                state.ServiceDetected = true;

            double currentWear = GetAverageTyreWear(telemetry);
            // LMU reports remaining tread as a fraction: a newly fitted set
            // increases this value, including when the compound stays the same.
            bool tyreWearImproved = IsFinite(state.PitEntryWear) &&
                IsFinite(currentWear) && currentWear - state.PitEntryWear >= 0.05;
            state.TyreServiceEvidenceSamples = tyreWearImproved
                ? state.TyreServiceEvidenceSamples + 1
                : 0;
            if (state.TyreServiceEvidenceSamples >= 2)
                state.ServiceDetected = true;

            byte[] oldCompounds = state.PitEntryCompounds;
            bool compoundsChanged = oldCompounds != null &&
                (oldCompounds[0] != 0 || oldCompounds[1] != 0 ||
                 oldCompounds[2] != 0 || oldCompounds[3] != 0) &&
                (telemetry.mTyreCompFL != 0 || telemetry.mTyreCompFR != 0 ||
                 telemetry.mTyreCompRL != 0 || telemetry.mTyreCompRR != 0) &&
                (oldCompounds[0] != telemetry.mTyreCompFL ||
                 oldCompounds[1] != telemetry.mTyreCompFR ||
                 oldCompounds[2] != telemetry.mTyreCompRL ||
                 oldCompounds[3] != telemetry.mTyreCompRR);
            state.CompoundServiceEvidenceSamples = compoundsChanged
                ? state.CompoundServiceEvidenceSamples + 1
                : 0;
            if (state.CompoundServiceEvidenceSamples >= 2)
                state.ServiceDetected = true;
        }

        private static double GetAverageTyreWear(LMUVehicleTelemetryRelative telemetry)
        {
            double fl = telemetry.mTyreWearFL;
            double fr = telemetry.mTyreWearFR;
            double rl = telemetry.mTyreWearRL;
            double rr = telemetry.mTyreWearRR;
            if (!IsFinite(fl) || !IsFinite(fr) || !IsFinite(rl) || !IsFinite(rr) ||
                fl < 0.0 || fl > 1.0 || fr < 0.0 || fr > 1.0 ||
                rl < 0.0 || rl > 1.0 || rr < 0.0 || rr > 1.0)
                return double.NaN;
            return (fl + fr + rl + rr) * 0.25;
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

            DateTime lastSeen = _lastKnownVehicleSeenUtcByPosition[index];
            return _lastKnownVehicleIdByPosition[index] >= 0 &&
                lastSeen != DateTime.MinValue &&
                DateTime.UtcNow - lastSeen <= SharedMemoryGracePeriod
                    ? _lastKnownVehicleIdByPosition[index]
                    : -1;
        }

        private void UpdatePreviousPosition(int vehicleId, int currentPosition)
        {
            if (vehicleId < 0 || currentPosition < 1)
                return;

            PreviousPositionState state;
            if (!_previousPositionByVehicleId.TryGetValue(vehicleId, out state))
            {
                _previousPositionByVehicleId[vehicleId] = new PreviousPositionState
                {
                    CurrentPosition = currentPosition,
                    PreviousPosition = currentPosition
                };
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (state.CurrentPosition != currentPosition)
            {
                state.PreviousPosition = state.CurrentPosition;
                state.CurrentPosition = currentPosition;
                state.ChangeTimeUtc = now;
                state.HoldUntilUtc = now.AddSeconds(_positionChangeColorCodingDurationSeconds);
                if (_positionChangeColorCodingDurationSeconds == 0)
                    state.PreviousPosition = currentPosition;
            }
            else if (now >= state.HoldUntilUtc)
            {
                state.PreviousPosition = currentPosition;
            }
        }

        private int GetPreviousPositionAtPosition(int position)
        {
            int vehicleId = GetVehicleIdAtPosition(position);
            PreviousPositionState state;
            if (vehicleId < 0 ||
                !_previousPositionByVehicleId.TryGetValue(vehicleId, out state))
                return _hasVehicle[position - 1]
                    ? _vehiclesByPosition[position - 1].mPlace
                    : 0;

            if (DateTime.UtcNow >= state.HoldUntilUtc)
                state.PreviousPosition = state.CurrentPosition;

            return state.PreviousPosition;
        }

        private void UpdateTrackLimitPoints(
            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById,
            LMUVehicleTelemetryRelative playerTelemetry,
            bool hasPlayerTelemetry)
        {
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

                _incidentsByPosition[positionIndex] = telemetry.mTrackLimitsSteps;

                if (tyreState.RefreshPending &&
                    _scoringInfo.mCurrentET >= tyreState.RefreshAfterET)
                {
                    string[] compounds = new[]
                    {
                        FormatTyreCompound(telemetry.mTyreCompFL),
                        FormatTyreCompound(telemetry.mTyreCompFR),
                        FormatTyreCompound(telemetry.mTyreCompRL),
                        FormatTyreCompound(telemetry.mTyreCompRR)
                    };
                    bool allCompoundsValid = true;
                    for (int wheel = 0; wheel < compounds.Length; wheel++)
                    {
                        if (string.IsNullOrEmpty(compounds[wheel]))
                        {
                            allCompoundsValid = false;
                            continue;
                        }

                        tyreState.Compounds[wheel] = compounds[wheel];
                    }

                    if (allCompoundsValid)
                    {
                        tyreState.RefreshPending = false;
                        tyreState.LastRefreshUtc = DateTime.UtcNow;
                    }
                }

            }

            int stepsPerPoint = _scoringInfo.mTrackLimitsStepsPerPoint;
            int stepsPerPenalty = _scoringInfo.mTrackLimitsStepsPerPenalty;

            // TinyPedal's LMU calculation normalizes both values with the
            // shared-memory steps-per-point value and uses 0 when unavailable.
            if (hasPlayerTelemetry &&
                playerTelemetry.mID == _lastPlayerVehicleId &&
                stepsPerPoint > 0)
            {
                _playerIncidents =
                    (double)playerTelemetry.mTrackLimitsSteps / stepsPerPoint;
            }
            _incidentsLimit = stepsPerPoint > 0
                ? (double)stepsPerPenalty / stepsPerPoint
                : 0.0;

            PluginPerformanceDiagnostics.TryLogTrackLimitSample(
                _playerTelemetryIndexForTrackLimits,
                _hasPlayerTelemetryHeaderForTrackLimits,
                _playerHasVehicleForTrackLimits,
                hasPlayerTelemetry,
                hasPlayerTelemetry ? playerTelemetry.mID : -1,
                _lastPlayerVehicleId,
                hasPlayerTelemetry ? playerTelemetry.mTrackLimitsSteps : -1,
                stepsPerPoint,
                stepsPerPenalty,
                _playerIncidents,
                _incidentsLimit
            );
        }

        private void ClearTrackLimitPoints()
        {
            _playerIncidents = 0.0;
            _incidentsLimit = 0.0;

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

        private Dictionary<int, LMUVehicleTelemetryRelative> ReadVehicleTelemetryRelativeById(
            out LMUVehicleTelemetryRelative playerTelemetry,
            out bool hasPlayerTelemetry,
            out int playerTelemetryIndex,
            out bool playerHasVehicle,
            out bool hasPlayerTelemetryHeader)
        {
            _telemetryByVehicleId.Clear();
            playerTelemetry = new LMUVehicleTelemetryRelative();
            hasPlayerTelemetry = false;

            hasPlayerTelemetryHeader = _sharedMemory.TryReadTelemetryPlayerIndex(
                out playerTelemetryIndex,
                out playerHasVehicle
            );

            for (int telemetryIndex = 0; telemetryIndex < MaxMappedVehicles; telemetryIndex++)
            {
                LMUVehicleTelemetryRelative telemetry;
                if (!_sharedMemory.TryReadVehicleTelemetryRelative(telemetryIndex, out telemetry))
                    continue;

                if (hasPlayerTelemetryHeader && playerHasVehicle &&
                    telemetryIndex == playerTelemetryIndex)
                {
                    playerTelemetry = telemetry;
                    hasPlayerTelemetry = true;
                }

                if (telemetry.mID >= 0 && !_telemetryByVehicleId.ContainsKey(telemetry.mID))
                    _telemetryByVehicleId.Add(telemetry.mID, telemetry);
            }

            return _telemetryByVehicleId;
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

        private void UpdateRelativeGaps(
            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById)
        {
            ClearRelativeGaps();

            double trackLength = _scoringInfo.mLapDist;

            if (trackLength <= 0.0 ||
                double.IsNaN(trackLength) ||
                double.IsInfinity(trackLength))
            {
                return;
            }

            LMUVehicleScoring player = new LMUVehicleScoring();
            bool hasPlayer = false;

            for (int positionIndex = 0; positionIndex < MaxVehicles; positionIndex++)
            {
                if (!_hasVehicle[positionIndex])
                    continue;

                LMUVehicleScoring vehicle = _vehiclesByPosition[positionIndex];
                if (vehicle.mIsPlayer)
                {
                    player = vehicle;
                    hasPlayer = true;
                }
            }

            if (!hasPlayer)
                return;

            List<RelativeVehicle> ahead = _relativeAheadCandidates;
            List<RelativeVehicle> behind = _relativeBehindCandidates;
            ahead.Clear();
            behind.Clear();

            for (int positionIndex = 0; positionIndex < MaxVehicles; positionIndex++)
            {
                if (!_hasVehicle[positionIndex])
                    continue;

                LMUVehicleScoring vehicle = _vehiclesByPosition[positionIndex];
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

            double[] nextAhead = _relativeAheadScratch;
            double[] nextBehind = _relativeBehindScratch;
            Array.Clear(nextAhead, 0, nextAhead.Length);
            Array.Clear(nextBehind, 0, nextBehind.Length);
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

                nextAhead[i] = cumulativeGap;
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

                nextBehind[i] = cumulativeGap;
            }

            Array.Copy(nextAhead, _relativeAhead, nextAhead.Length);
            Array.Copy(nextBehind, _relativeBehind, nextBehind.Length);
        }

        private Dictionary<int, LMUVehicleTelemetryRelative> ReadRelativeVehicleTelemetryById()
        {
            _relativeTelemetryByVehicleId.Clear();

            for (int telemetryIndex = 0; telemetryIndex < MaxMappedVehicles; telemetryIndex++)
            {
                LMUVehicleTelemetryRelative telemetry;
                if (!_sharedMemory.TryReadVehicleTelemetryRelative(telemetryIndex, out telemetry))
                    continue;

                if (telemetry.mID >= 0 &&
                    !_relativeTelemetryByVehicleId.ContainsKey(telemetry.mID))
                {
                    _relativeTelemetryByVehicleId.Add(telemetry.mID, telemetry);
                }
            }

            return _relativeTelemetryByVehicleId;
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
        private struct RelativeVehicle
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
            _teamSpeak.Dispose();
            _standingsApi.Dispose();
            _sharedMemory.Dispose();
        }
    }

    internal sealed class TeamSpeakClientQuery : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Thread _worker;
        private readonly Thread _talkWorker;
        private volatile bool _stopping;
        private string _apiKey = "";
        private DateTime _lastSuccessfulPollUtc = DateTime.MinValue;
        private string _activeSchandlerId = "";
        private readonly Dictionary<string, bool> _talkingByClientId = new Dictionary<string, bool>();
        private Snapshot _snapshot = new Snapshot();

        private sealed class Snapshot
        {
            public bool Connected;
            public string Status = "Not configured";
            public string Server = "";
            public string Channel = "";
            public string Player = "";
            public int PlayerStatus;
            public bool PlayerTalking;
            public int Count;
            public readonly string[] Names = new string[15];
            public readonly int[] Statuses = new int[15];
            public readonly bool[] Talking = new bool[15];
            public readonly string[] ClientIds = new string[15];
            public string PlayerId = "";
            public string SchandlerId = "";
        }

        public TeamSpeakClientQuery()
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MMO TeamSpeak ClientQuery" };
            _talkWorker = new Thread(TalkStatusWorkerLoop) { IsBackground = true, Name = "MMO TeamSpeak Talk Status" };
            _worker.Start();
            _talkWorker.Start();
        }

        public void Configure(string key)
        {
            _apiKey = key ?? "";
            if (string.IsNullOrEmpty(_apiKey))
            {
                lock (_gate)
                    _lastSuccessfulPollUtc = DateTime.MinValue;
            }
        }
        public bool Connected
        {
            get
            {
                lock (_gate)
                {
                    return _snapshot.Connected ||
                        (_lastSuccessfulPollUtc != DateTime.MinValue &&
                         DateTime.UtcNow - _lastSuccessfulPollUtc <= TimeSpan.FromSeconds(5));
                }
            }
        }
        public string Status { get { lock (_gate) return _snapshot.Status; } }
        public string ServerName { get { lock (_gate) return _snapshot.Server; } }
        public string ChannelName { get { lock (_gate) return _snapshot.Channel; } }
        public string PlayerName { get { lock (_gate) return _snapshot.Player; } }
        public int PlayerStatus { get { lock (_gate) return _snapshot.PlayerStatus; } }
        public bool PlayerTalking { get { lock (_gate) return _snapshot.PlayerTalking; } }
        public int ClientCount { get { lock (_gate) return _snapshot.Count; } }
        public string GetClientName(int index) { lock (_gate) return index > 0 && index <= 15 ? _snapshot.Names[index - 1] ?? "" : ""; }
        public int GetClientStatus(int index) { lock (_gate) return index > 0 && index <= 15 ? _snapshot.Statuses[index - 1] : 0; }
        public bool GetClientTalking(int index) { lock (_gate) return index > 0 && index <= 15 && _snapshot.Talking[index - 1]; }

        private void WorkerLoop()
        {
            while (!_stopping)
            {
                string key = _apiKey;
                if (string.IsNullOrEmpty(key))
                {
                    Publish(new Snapshot { Status = "Not configured" });
                    Thread.Sleep(500);
                    continue;
                }
                try { Poll(key); }
                catch (Exception ex)
                {
                    Publish(new Snapshot { Status = "ClientQuery unavailable: " + ex.Message });
                }
                Thread.Sleep(1000);
            }
        }

        private void Poll(string key)
        {
            using (TcpClient client = new TcpClient())
            {
                client.ReceiveTimeout = 3500;
                client.SendTimeout = 3500;
                client.Connect("127.0.0.1", 25639);
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\r\n" })
                {
                    // ClientQuery 1.5 sends a fixed four-line greeting. Do not wait
                    // for a blank line: the greeting has no blank-line terminator.
                    for (int greetingLine = 0; greetingLine < 4; greetingLine++)
                    {
                        if (reader.ReadLine() == null)
                            throw new IOException("ClientQuery closed during its greeting");
                    }
                    Send(writer, reader, "auth apikey=" + Escape(key));
                    string selectedHandler = Value(ParseRecords(Send(writer, reader, "use")), "schandlerid");
                    lock (_gate) _activeSchandlerId = selectedHandler;
                    List<Dictionary<string, string>> who = ParseRecords(Send(writer, reader, "whoami"));
                    if (who.Count == 0) throw new IOException("No active TeamSpeak server");
                    string clid = Get(who[0], "clid");
                    string channelId = Get(who[0], "client_channel_id");
                    if (string.IsNullOrEmpty(channelId)) channelId = Get(who[0], "cid");
                    List<Dictionary<string, string>> clients = ParseRecords(Send(writer, reader, "clientlist -voice"));
                    Dictionary<string, string> own = null;
                    List<Dictionary<string, string>> channelClients = new List<Dictionary<string, string>>();
                    foreach (Dictionary<string, string> row in clients)
                    {
                        if (Get(row, "clid") == clid) own = row;
                        if (Get(row, "cid") == channelId) channelClients.Add(row);
                    }
                    List<Dictionary<string, string>> serverInfo = ParseRecords(Send(writer, reader, "servervariable virtualserver_name"));
                    List<Dictionary<string, string>> channelInfo = string.IsNullOrEmpty(channelId)
                        ? new List<Dictionary<string, string>>()
                        : ParseRecords(Send(writer, reader, "channelvariable cid=" + Escape(channelId) + " channel_name"));
                    Snapshot result = new Snapshot { Connected = true, Status = "Connected", Server = Value(serverInfo, "virtualserver_name"), Channel = Value(channelInfo, "channel_name"), Count = channelClients.Count, SchandlerId = selectedHandler };
                    if (own != null)
                    {
                        result.Player = Decode(Get(own, "client_nickname"));
                        result.PlayerStatus = StatusBits(own);
                        result.PlayerId = Get(own, "clid");
                    }
                    if (string.IsNullOrEmpty(result.Player))
                        result.Player = Decode(Get(who[0], "client_nickname"));
                    for (int i = 0; i < Math.Min(15, channelClients.Count); i++)
                    {
                        Dictionary<string, string> row = channelClients[i];
                        result.Names[i] = Decode(Get(row, "client_nickname"));
                        result.Statuses[i] = StatusBits(row);
                        result.ClientIds[i] = Get(row, "clid");
                    }
                    RefreshTalkSnapshot(result, clients);
                    Publish(result);
                }
            }
        }

        private static int StatusBits(Dictionary<string, string> row)
        {
            int value = 0;
            if (Get(row, "client_input_muted") == "1") value |= 1;
            if (Get(row, "client_output_muted") == "1") value |= 2;
            return value;
        }

        private void RefreshTalkSnapshot(Snapshot snapshot, List<Dictionary<string, string>> clients)
        {
            lock (_gate)
            {
                HashSet<string> currentIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (Dictionary<string, string> row in clients)
                {
                    string id = Get(row, "clid");
                    if (string.IsNullOrEmpty(id)) continue;
                    currentIds.Add(id);
                    if (!_talkingByClientId.ContainsKey(id))
                        _talkingByClientId[id] = Get(row, "client_flag_talking") == "1";
                }
                List<string> stale = new List<string>();
                foreach (string id in _talkingByClientId.Keys)
                    if (!currentIds.Contains(id)) stale.Add(id);
                foreach (string id in stale) _talkingByClientId.Remove(id);

                bool playerTalking;
                if (_talkingByClientId.TryGetValue(snapshot.PlayerId, out playerTalking))
                    snapshot.PlayerTalking = playerTalking;
                for (int i = 0; i < snapshot.ClientIds.Length; i++)
                {
                    bool talking;
                    if (_talkingByClientId.TryGetValue(snapshot.ClientIds[i] ?? "", out talking))
                        snapshot.Talking[i] = talking;
                }
            }
        }

        private void TalkStatusWorkerLoop()
        {
            while (!_stopping)
            {
                string key = _apiKey;
                if (string.IsNullOrEmpty(key)) { Thread.Sleep(500); continue; }
                try { WatchTalkStatus(key); }
                catch { if (!_stopping) Thread.Sleep(1000); }
            }
        }

        private void WatchTalkStatus(string key)
        {
            using (TcpClient client = new TcpClient())
            {
                client.ReceiveTimeout = 1000;
                client.SendTimeout = 3500;
                client.Connect("127.0.0.1", 25639);
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\r\n" })
                {
                    for (int i = 0; i < 4; i++)
                        if (reader.ReadLine() == null) throw new IOException("ClientQuery closed during its greeting");
                    Send(writer, reader, "auth apikey=" + Escape(key));
                    Send(writer, reader, "use");
                    Send(writer, reader, "clientnotifyregister schandlerid=0 event=notifytalkstatuschange");
                    while (!_stopping && string.Equals(key, _apiKey, StringComparison.Ordinal))
                    {
                        string line;
                        try { line = reader.ReadLine(); }
                        catch (IOException ex)
                        {
                            SocketException socketError = ex.InnerException as SocketException;
                            if (socketError != null && (socketError.SocketErrorCode == SocketError.TimedOut || socketError.SocketErrorCode == SocketError.WouldBlock))
                                continue;
                            throw;
                        }
                        if (line == null) throw new IOException("ClientQuery event connection closed");
                        if (!line.StartsWith("notifytalkstatuschange ", StringComparison.Ordinal)) continue;
                        List<Dictionary<string, string>> records = ParseRecords(line);
                        if (records.Count == 0) continue;
                        Dictionary<string, string> notification = records[0];
                        string handler = Get(notification, "schandlerid");
                        string clientId = Get(notification, "clid");
                        int talkStatus;
                        if (string.IsNullOrEmpty(clientId) || !int.TryParse(Get(notification, "status"), out talkStatus)) continue;
                        lock (_gate)
                        {
                            if (!string.IsNullOrEmpty(handler) && !string.IsNullOrEmpty(_activeSchandlerId) && handler != _activeSchandlerId)
                                continue;
                            _talkingByClientId[clientId] = talkStatus == 1;
                            if (_snapshot.PlayerId == clientId) _snapshot.PlayerTalking = talkStatus == 1;
                            for (int i = 0; i < _snapshot.ClientIds.Length; i++)
                                if (_snapshot.ClientIds[i] == clientId) _snapshot.Talking[i] = talkStatus == 1;
                        }
                    }
                }
            }
        }

        private static string Send(StreamWriter writer, StreamReader reader, string command)
        {
            writer.WriteLine(command);
            StringBuilder response = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (response.Length > 0) response.Append(' ');
                response.Append(line);
                if (line.StartsWith("error id=", StringComparison.Ordinal))
                {
                    if (!line.StartsWith("error id=0 ", StringComparison.Ordinal))
                        throw new IOException("ClientQuery command '" + command + "' failed: " + line);
                    return response.ToString();
                }
            }
            throw new IOException("ClientQuery connection closed");
        }

        private static List<Dictionary<string, string>> ParseRecords(string response)
        {
            List<Dictionary<string, string>> result = new List<Dictionary<string, string>>();
            string data = Regex.Replace(response ?? "", @"\s*error id=[^\s]+.*$", "");
            foreach (string record in data.Split('|'))
            {
                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string item in record.Split(' '))
                {
                    int equals = item.IndexOf('=');
                    if (equals > 0) values[item.Substring(0, equals)] = Unescape(item.Substring(equals + 1));
                }
                if (values.Count > 0) result.Add(values);
            }
            return result;
        }

        private static string Value(List<Dictionary<string, string>> rows, string key) { return rows.Count == 0 ? "" : Get(rows[0], key); }
        private static string Get(Dictionary<string, string> row, string key) { string value; return row != null && row.TryGetValue(key, out value) ? value : ""; }
        private static string Escape(string value) { return (value ?? "").Replace("\\", "\\\\").Replace(" ", "\\s").Replace("|", "\\p"); }
        private static string Decode(string value) { return Unescape(value); }
        private static string Unescape(string value)
        {
            return (value ?? "").Replace("\\s", " ").Replace("\\p", "|").Replace("\\/", "/").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r");
        }
        private void Publish(Snapshot snapshot)
        {
            lock (_gate)
            {
                if (snapshot.Connected)
                    _lastSuccessfulPollUtc = DateTime.UtcNow;
                bool talking;
                if (_talkingByClientId.TryGetValue(snapshot.PlayerId ?? "", out talking))
                    snapshot.PlayerTalking = talking;
                for (int i = 0; i < snapshot.ClientIds.Length; i++)
                    if (_talkingByClientId.TryGetValue(snapshot.ClientIds[i] ?? "", out talking))
                        snapshot.Talking[i] = talking;
                _snapshot = snapshot;
            }
        }
        public void Dispose()
        {
            _stopping = true;
            if (_worker != null && _worker.IsAlive) _worker.Join(1500);
            if (_talkWorker != null && _talkWorker.IsAlive) _talkWorker.Join(1500);
        }
    }

    internal sealed class MMOPluginSettingsControl : UserControl
    {
        private static readonly SolidColorBrush AccentBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0xB2, 0xAA));
        private static readonly SolidColorBrush InactiveTabBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0xBA, 0xBA, 0xBA));
        private static readonly SolidColorBrush SurfaceBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x2F, 0x32));
        private static readonly SolidColorBrush ThemeBorderBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x45, 0x4C, 0x50));
        private static readonly SolidColorBrush AccentWashBrush =
            new SolidColorBrush(Color.FromArgb(0x30, 0x20, 0xB2, 0xAA));

        private const string LatestReleaseApi = "https://api.github.com/repos/St54Kevin/MMOPlugin/releases/latest";
        private readonly PluginManager _pluginManager;
        private readonly List<string> _propertyNames;
        private readonly ObservableCollection<MMOPropertyDisplayItem> _displayedProperties =
            new ObservableCollection<MMOPropertyDisplayItem>();
        private readonly TextBox _searchBox;
        private readonly CheckBox _hideEmptyPropertiesOption;
        private readonly ListView _propertyList;
        private readonly TextBlock _resultCount;
        private readonly DispatcherTimer _refreshTimer;
        private readonly TextBlock _updateStatus;
        private readonly Button _checkUpdateButton;
        private readonly Button _installUpdateButton;
        private readonly CarTrackStatisticsStore _statisticsStore;
        private TabItem _pluginTab;
        private TabItem _dataTab;
        private ComboBox _dataCarFilter;
        private ComboBox _dataTrackFilter;
        private ListView _dataList;
        private TextBlock _dataStatus;
        private TextBlock _changelogStatus;
        private StackPanel _changelogEntries;
        private TabItem _changelogTab;
        private bool _loadingChangelog;
        private bool _changelogLoaded;
        private bool _refreshingData;
        private Border _pluginUpdateBadge;
        private Button _checkOverlaysButton;
        private Button _installAllOverlaysButton;
        private Button _updateAllOverlaysButton;
        private TextBlock _overlayStatus;
        private TextBlock _pluginUpdateFirstHint;
        private ListView _overlayList;
        private TabItem _overlaysTab;
        private Border _overlayUpdateBadge;
        private readonly List<OverlayPackageItem> _overlayItems =
            new List<OverlayPackageItem>();
        private string _overlayPackageVersion;
        private readonly CheckBox _leaderboardTeamNamesOption;
        private readonly CheckBox _leaderboardPositionsGainedOption;
        private readonly CheckBox _additionalRelativeTeamNamesOption;
        private readonly CheckBox _leaderboardPitDataOption;
        private readonly CheckBox _relativePitDataOption;
        private readonly CheckBox _debuggingOption;
        private readonly ComboBox _tyreCompoundSymbolOption;
        private readonly ComboBox _lastLaptimeDisplayDurationOption;
        private readonly ComboBox _positionChangeColorCodingOption;
        private readonly Action<bool> _setLeaderboardTeamNamesOption;
        private readonly Action<bool> _setShowPositionsGained;
        private readonly Action<int> _setPositionChangeColorCodingDuration;
        private readonly Action<bool> _setAdditionalRelativeTeamNamesOption;
        private readonly Action<int> _setTyreCompoundSymbolMode;
        private readonly Action<int> _setLastLaptimeDisplayDuration;
        private readonly Action<bool> _setShowLastPitLapAndTimeInLeaderboard;
        private readonly Action<bool> _setShowLastPitLapAndTimeInRelative;
        private readonly Func<string> _getTeamSpeakApiKey;
        private readonly Action<string> _setTeamSpeakApiKey;
        private readonly Action<bool> _setDebuggingEnabled;
        private readonly PasswordBox _teamSpeakKeyPassword;
        private readonly TextBox _teamSpeakKeyVisible;
        private readonly CheckBox _showTeamSpeakKey;
        private TabItem _settingsTab;
        private GitHubRelease _latestRelease;
        private bool _checkingUpdate;
        private bool _installingUpdate;
        private bool _pluginUpdateStatusKnown;
        private bool _pluginUpdateAvailable;
        private bool _checkingOverlays;
        private bool _installingOverlays;
        private Window _hostWindow;
        private bool _settingsControlLoaded;

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
            bool showPositionsGained,
            Action<bool> setShowPositionsGained,
            int positionChangeColorCodingDurationSeconds,
            Action<int> setPositionChangeColorCodingDuration,
            bool showLastPitLapAndTimeInRelative,
            Action<bool> setShowLastPitLapAndTimeInRelative,
            int lastLaptimeDisplayDurationMs,
            Action<int> setLastLaptimeDisplayDuration,
            Func<string> getTeamSpeakApiKey,
            Action<string> setTeamSpeakApiKey,
            bool debuggingEnabled,
            Action<bool> setDebuggingEnabled,
            CarTrackStatisticsStore statisticsStore)
        {
            _pluginManager = pluginManager;
            _statisticsStore = statisticsStore;
            CleanupOverlayUpdaterTempDirectory();
            _propertyNames = propertyNames ?? new List<string>();
            _setLeaderboardTeamNamesOption = setLeaderboardTeamNamesOption;
            _setAdditionalRelativeTeamNamesOption = setAdditionalRelativeTeamNamesOption;
            _setTyreCompoundSymbolMode = setTyreCompoundSymbolMode;
            _setLastLaptimeDisplayDuration = setLastLaptimeDisplayDuration;
            _setShowLastPitLapAndTimeInLeaderboard = setShowLastPitLapAndTimeInLeaderboard;
            _setShowPositionsGained = setShowPositionsGained;
            _setPositionChangeColorCodingDuration = setPositionChangeColorCodingDuration;
            _setShowLastPitLapAndTimeInRelative = setShowLastPitLapAndTimeInRelative;
            _getTeamSpeakApiKey = getTeamSpeakApiKey;
            _setTeamSpeakApiKey = setTeamSpeakApiKey;
            _setDebuggingEnabled = setDebuggingEnabled;
            Background = Brushes.Transparent;
            Foreground = Brushes.White;
            FontSize = 12;
            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8, 5, 8, 5),
                ToolTip = "Search MMO properties",
                MinWidth = 220,
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontSize = 12,
                Background = SurfaceBrush,
                BorderBrush = ThemeBorderBrush,
                CaretBrush = AccentBrush
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
            _leaderboardPositionsGainedOption = new CheckBox
            {
                Content = "Show Positions gained",
                IsChecked = showPositionsGained,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6),
                VerticalAlignment = VerticalAlignment.Top
            };
            _positionChangeColorCodingOption = new ComboBox
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _positionChangeColorCodingOption.Items.Add("0s");
            _positionChangeColorCodingOption.Items.Add("3s");
            _positionChangeColorCodingOption.Items.Add("5s");
            int[] positionChangeDurations = { 0, 3, 5 };
            _positionChangeColorCodingOption.SelectedIndex =
                Array.IndexOf(positionChangeDurations, positionChangeColorCodingDurationSeconds);
            if (_positionChangeColorCodingOption.SelectedIndex < 0)
                _positionChangeColorCodingOption.SelectedIndex = 1;
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
                Content = "Show last pit lap and time",
                IsChecked = showLastPitLapAndTimeInLeaderboard,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6)
            };
            _relativePitDataOption = new CheckBox
            {
                Content = "Show last pit lap and time",
                IsChecked = showLastPitLapAndTimeInRelative,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6)
            };
            _teamSpeakKeyPassword = new PasswordBox { MinWidth = 280, Password = _getTeamSpeakApiKey() ?? "" };
            _teamSpeakKeyVisible = new TextBox { MinWidth = 280, Text = _getTeamSpeakApiKey() ?? "", Visibility = Visibility.Collapsed };
            _showTeamSpeakKey = new CheckBox { Content = "Show key", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _hideEmptyPropertiesOption = new CheckBox
            {
                Content = "Hide empty properties",
                IsChecked = true,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            _debuggingOption = new CheckBox
            {
                Content = "Debugging",
                IsChecked = debuggingEnabled,
                FontSize = 13,
                Margin = new Thickness(0, 16, 0, 4),
                VerticalAlignment = VerticalAlignment.Top
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
                BorderBrush = ThemeBorderBrush,
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
            downloadLink.Foreground = AccentBrush;

            System.Windows.Documents.Hyperlink releasesLink =
                new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("GitHub")
                )
                {
                    NavigateUri = new Uri("https://github.com/St54Kevin/MMOPlugin")
                };
            releasesLink.RequestNavigate += DownloadLink_RequestNavigate;
            releasesLink.Foreground = AccentBrush;

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
                Foreground = AccentBrush,
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
                BorderBrush = ThemeBorderBrush,
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

            DockPanel propertyHeading = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 3)
            };
            _hideEmptyPropertiesOption.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(_hideEmptyPropertiesOption, Dock.Right);
            propertyHeading.Children.Add(_hideEmptyPropertiesOption);
            StackPanel propertyHeadingText = new StackPanel { Orientation = Orientation.Horizontal };
            propertyHeadingText.Children.Add(new TextBlock
            {
                Text = "Properties",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            propertyHeadingText.Children.Add(_resultCount);
            propertyHeadingText.Children.Add(new TextBlock
            {
                Text = "· 10s refresh interval",
                Opacity = 0.6,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            propertyHeading.Children.Add(propertyHeadingText);
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
            _hideEmptyPropertiesOption.Checked += HideEmptyPropertiesOption_Changed;
            _hideEmptyPropertiesOption.Unchecked += HideEmptyPropertiesOption_Changed;
            _checkUpdateButton.Click += CheckUpdateButton_Click;
            _installUpdateButton.Click += InstallUpdateButton_Click;
            _leaderboardTeamNamesOption.Checked += TeamNameOption_Changed;
            _leaderboardTeamNamesOption.Unchecked += TeamNameOption_Changed;
            _leaderboardPositionsGainedOption.Checked += TeamNameOption_Changed;
            _leaderboardPositionsGainedOption.Unchecked += TeamNameOption_Changed;
            _additionalRelativeTeamNamesOption.Checked += TeamNameOption_Changed;
            _additionalRelativeTeamNamesOption.Unchecked += TeamNameOption_Changed;
            _leaderboardPitDataOption.Checked += PitDataOption_Changed;
            _leaderboardPitDataOption.Unchecked += PitDataOption_Changed;
            _tyreCompoundSymbolOption.SelectionChanged += TyreCompoundSymbolOption_SelectionChanged;
            _lastLaptimeDisplayDurationOption.SelectionChanged += LastLaptimeDisplayDurationOption_SelectionChanged;
            _positionChangeColorCodingOption.SelectionChanged += PositionChangeColorCodingOption_SelectionChanged;
            _debuggingOption.Checked += DebuggingOption_Changed;
            _debuggingOption.Unchecked += DebuggingOption_Changed;
            Loaded += SettingsControl_Loaded;
            Unloaded += SettingsControl_Unloaded;

            _refreshTimer = new DispatcherTimer(
                DispatcherPriority.Background
            )
            {
                Interval = TimeSpan.FromSeconds(10)
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
            StackPanel duringRaceOptions = new StackPanel();
            duringRaceOptions.Children.Add(_leaderboardPitDataOption);
            duringRaceOptions.Children.Add(_leaderboardPositionsGainedOption);
            duringRaceOptions.Children.Add(new TextBlock
            {
                Text = "Position Change Color Coding:",
                FontSize = 13,
                Foreground = AccentBrush,
                Margin = new Thickness(0, 12, 0, 6)
            });
            duringRaceOptions.Children.Add(_positionChangeColorCodingOption);
            duringRaceOptions.Children.Add(new TextBlock
            {
                Text = "Green & Red Color Coding to names in race depending on Pos change",
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(0, 4, 0, 0)
            });
            leaderboardOptions.Children.Add(new GroupBox
            {
                Header = "During Race",
                Content = duringRaceOptions,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 0)
            });

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
            StackPanel generalOptions = new StackPanel();
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Tyre Compound Symbol:",
                FontSize = 13,
                Foreground = AccentBrush,
                Margin = new Thickness(0, 0, 0, 6)
            });
            generalOptions.Children.Add(_tyreCompoundSymbolOption);
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Last Laptime display duration",
                FontSize = 13,
                Foreground = AccentBrush,
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
            generalOptions.Children.Add(new TextBlock { Text = "TeamSpeak ClientQuery API Key", FontSize = 13, Foreground = AccentBrush, Margin = new Thickness(0, 16, 0, 6) });
            StackPanel keyInput = new StackPanel { Orientation = Orientation.Horizontal };
            keyInput.Children.Add(_teamSpeakKeyPassword);
            keyInput.Children.Add(_teamSpeakKeyVisible);
            keyInput.Children.Add(_showTeamSpeakKey);
            generalOptions.Children.Add(keyInput);
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Connects to the local TeamSpeak ClientQuery service. The key is protected for this Windows user.",
                Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 4, 0, 0)
            });
            generalOptions.Children.Add(_debuggingOption);
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Enables performance diagnostics in the SimHub log.",
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(22, 0, 0, 0)
            });

            StackPanel settingsPage = new StackPanel
            {
                Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent
            };
            Style sectionStyle = new Style(typeof(GroupBox));
            sectionStyle.Setters.Add(new Setter(Control.ForegroundProperty, AccentBrush));
            sectionStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            sectionStyle.Setters.Add(new Setter(Control.BorderBrushProperty, AccentBrush));
            sectionStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            sectionStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10)));
            ControlTemplate sectionTemplate = new ControlTemplate(typeof(GroupBox));
            FrameworkElementFactory sectionRoot = new FrameworkElementFactory(typeof(StackPanel));
            FrameworkElementFactory sectionHeader = new FrameworkElementFactory(typeof(ContentPresenter));
            sectionHeader.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            sectionHeader.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 5));
            sectionHeader.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            sectionHeader.SetValue(TextElement.FontSizeProperty, 14.0);
            sectionHeader.SetValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
            sectionRoot.AppendChild(sectionHeader);
            FrameworkElementFactory sectionFrame = new FrameworkElementFactory(typeof(Border));
            sectionFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            sectionFrame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            sectionFrame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            sectionFrame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            sectionFrame.SetValue(Border.PaddingProperty, new Thickness(12));
            FrameworkElementFactory sectionContent = new FrameworkElementFactory(typeof(ContentPresenter));
            sectionContent.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            sectionFrame.AppendChild(sectionContent);
            sectionRoot.AppendChild(sectionFrame);
            sectionTemplate.VisualTree = sectionRoot;
            sectionStyle.Setters.Add(new Setter(Control.TemplateProperty, sectionTemplate));
            settingsPage.Resources.Add(typeof(GroupBox), sectionStyle);
            settingsPage.Children.Add(new GroupBox
            {
                Header = "Leaderboard",
                Content = leaderboardOptions,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });
            settingsPage.Children.Add(new GroupBox
            {
                Header = "Relative",
                Content = relativeOptions,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });
            settingsPage.Children.Add(new GroupBox
            {
                Header = "General",
                Content = generalOptions,
                FontSize = 14
            });

            Style tabItemStyle = new Style(typeof(TabItem));
            tabItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            tabItemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            tabItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 7, 18, 6)));
            tabItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, InactiveTabBrush));
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
            selectionIndicator.SetValue(Border.BackgroundProperty, AccentBrush);
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
                Setters = { new Setter(Control.ForegroundProperty, AccentBrush) }
            });
            tabItemStyle.Triggers.Add(new Trigger
            {
                Property = TabItem.IsSelectedProperty,
                Value = true,
                Setters = { new Setter(Control.ForegroundProperty, AccentBrush) }
            });

            TabControl tabs = new TabControl { ItemContainerStyle = tabItemStyle };
            ApplyMenuTheme(tabs);
            _pluginTab = new TabItem
            {
                Header = CreateUpdateTabHeader("Plugin", out _pluginUpdateBadge),
                Content = root
            };
            tabs.Items.Add(_pluginTab);
            BuildOverlaysPage(tabs);
            _settingsTab = new TabItem
            {
                Header = CreateTabHeader("Settings"),
                Content = new ScrollViewer
                {
                    Content = settingsPage,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    CanContentScroll = false,
                    VerticalAlignment = VerticalAlignment.Stretch
                }
            };
            tabs.Items.Add(_settingsTab);
            BuildControlsPage(tabs);
            BuildDataPage(tabs);
            BuildChangelogPage(tabs);
            ApplyDropdownTheme(_tyreCompoundSymbolOption, tabs);
            ApplyDropdownTheme(_lastLaptimeDisplayDurationOption, tabs);
            ApplyDropdownTheme(_positionChangeColorCodingOption, tabs);
            ApplyDropdownTheme(_dataCarFilter, tabs);
            ApplyDropdownTheme(_dataTrackFilter, tabs);
            tabs.SelectionChanged += PluginTabs_SelectionChanged;

            _teamSpeakKeyPassword.PasswordChanged += (s, e) =>
            {
                if (_teamSpeakKeyVisible.Text != _teamSpeakKeyPassword.Password)
                    _teamSpeakKeyVisible.Text = _teamSpeakKeyPassword.Password;
                _setTeamSpeakApiKey(_teamSpeakKeyPassword.Password);
            };
            _teamSpeakKeyVisible.TextChanged += (s, e) =>
            {
                if (_teamSpeakKeyPassword.Password != _teamSpeakKeyVisible.Text)
                    _teamSpeakKeyPassword.Password = _teamSpeakKeyVisible.Text;
                _setTeamSpeakApiKey(_teamSpeakKeyVisible.Text);
            };
            _showTeamSpeakKey.Checked += (s, e) => SetTeamSpeakKeyVisible(true);
            _showTeamSpeakKey.Unchecked += (s, e) => SetTeamSpeakKeyVisible(false);

            Content = tabs;
            ApplyPropertyFilter();
        }

        private static void ApplyMenuTheme(TabControl tabs)
        {
            Style textBlockStyle = new Style(typeof(TextBlock));
            textBlockStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
            textBlockStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
            tabs.Resources.Add(typeof(TextBlock), textBlockStyle);

            Style buttonStyle = new Style(typeof(Button));
            buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBrush));
            buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBorderBrush));
            buttonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            buttonStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
            buttonStyle.Triggers.Add(new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
                Setters =
                {
                    new Setter(Control.BackgroundProperty, AccentWashBrush),
                    new Setter(Control.BorderBrushProperty, AccentBrush)
                }
            });
            tabs.Resources.Add(typeof(Button), buttonStyle);

            Style fieldStyle = new Style(typeof(TextBox));
            fieldStyle.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBrush));
            fieldStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBorderBrush));
            fieldStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            fieldStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            fieldStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            tabs.Resources.Add(typeof(TextBox), fieldStyle);

            Style comboStyle = new Style(typeof(ComboBox));
            comboStyle.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBrush));
            comboStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBorderBrush));
            comboStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            comboStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 4, 7, 4)));
            ControlTemplate comboTemplate = new ControlTemplate(typeof(ComboBox));
            FrameworkElementFactory comboRoot = new FrameworkElementFactory(typeof(Grid));

            FrameworkElementFactory comboFrame = new FrameworkElementFactory(typeof(Border));
            comboFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            comboFrame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            comboFrame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            comboFrame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            comboRoot.AppendChild(comboFrame);

            FrameworkElementFactory comboContentGrid = new FrameworkElementFactory(typeof(Grid));
            comboContentGrid.SetValue(FrameworkElement.MarginProperty, new Thickness(1));

            FrameworkElementFactory selectedContent = new FrameworkElementFactory(typeof(ContentPresenter));
            selectedContent.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 32, 0));
            selectedContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            selectedContent.SetValue(UIElement.IsHitTestVisibleProperty, false);
            selectedContent.SetBinding(
                ContentPresenter.ContentProperty,
                new Binding("SelectionBoxItem") { RelativeSource = RelativeSource.TemplatedParent }
            );
            selectedContent.SetBinding(
                ContentPresenter.ContentTemplateProperty,
                new Binding("SelectionBoxItemTemplate") { RelativeSource = RelativeSource.TemplatedParent }
            );
            selectedContent.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            selectedContent.SetValue(TextElement.FontSizeProperty, new TemplateBindingExtension(Control.FontSizeProperty));

            FrameworkElementFactory dropButton = new FrameworkElementFactory(typeof(ToggleButton));
            dropButton.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            dropButton.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            dropButton.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            dropButton.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            dropButton.SetValue(Control.ForegroundProperty, InactiveTabBrush);
            dropButton.SetValue(FrameworkElement.FocusableProperty, false);
            dropButton.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
            dropButton.SetValue(ContentControl.ContentProperty, "⌄");
            dropButton.SetBinding(
                ToggleButton.IsCheckedProperty,
                new Binding("IsDropDownOpen")
                {
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.FindAncestor,
                        typeof(ComboBox),
                        1
                    ),
                    Mode = BindingMode.TwoWay
                }
            );
            ControlTemplate dropButtonTemplate = new ControlTemplate(typeof(ToggleButton));
            FrameworkElementFactory dropButtonRoot = new FrameworkElementFactory(typeof(Border));
            dropButtonRoot.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            FrameworkElementFactory arrowPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            arrowPresenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            arrowPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrowPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrowPresenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            arrowPresenter.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            arrowPresenter.SetValue(TextElement.FontSizeProperty, new TemplateBindingExtension(Control.FontSizeProperty));
            dropButtonRoot.AppendChild(arrowPresenter);
            dropButtonTemplate.VisualTree = dropButtonRoot;
            dropButton.SetValue(Control.TemplateProperty, dropButtonTemplate);
            comboContentGrid.AppendChild(dropButton);
            comboContentGrid.AppendChild(selectedContent);
            comboRoot.AppendChild(comboContentGrid);

            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.FocusableProperty, false);
            popup.SetValue(Popup.StaysOpenProperty, false);
            popup.SetBinding(
                Popup.IsOpenProperty,
                new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent }
            );
            popup.SetBinding(
                Popup.PlacementTargetProperty,
                new Binding { RelativeSource = RelativeSource.TemplatedParent }
            );

            FrameworkElementFactory popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            popupBorder.SetValue(Border.BackgroundProperty, SurfaceBrush);
            popupBorder.SetValue(Border.BorderBrushProperty, AccentBrush);
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(FrameworkElement.MinWidthProperty, new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));
            popupBorder.SetBinding(
                FrameworkElement.MaxHeightProperty,
                new Binding("MaxDropDownHeight") { RelativeSource = RelativeSource.TemplatedParent }
            );

            FrameworkElementFactory popupScroll = new FrameworkElementFactory(typeof(ScrollViewer));
            popupScroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            popupScroll.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);
            popupScroll.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Contained);
            FrameworkElementFactory popupItems = new FrameworkElementFactory(typeof(ItemsPresenter));
            popupItems.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);
            popupScroll.AppendChild(popupItems);
            popupBorder.AppendChild(popupScroll);
            popup.AppendChild(popupBorder);
            comboRoot.AppendChild(popup);

            comboTemplate.VisualTree = comboRoot;
            comboStyle.Setters.Add(new Setter(Control.TemplateProperty, comboTemplate));
            tabs.Resources.Add(typeof(ComboBox), comboStyle);

            Style comboItemStyle = new Style(typeof(ComboBoxItem));
            comboItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBrush));
            comboItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            comboItemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            comboItemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            comboItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            comboItemStyle.Triggers.Add(new Trigger
            {
                Property = ComboBoxItem.IsHighlightedProperty,
                Value = true,
                Setters =
                {
                    new Setter(Control.BackgroundProperty, AccentWashBrush),
                    new Setter(Control.BorderBrushProperty, AccentBrush),
                    new Setter(Control.ForegroundProperty, Brushes.White)
                }
            });
            comboItemStyle.Triggers.Add(new Trigger
            {
                Property = ComboBoxItem.IsSelectedProperty,
                Value = true,
                Setters =
                {
                    new Setter(Control.BackgroundProperty, AccentWashBrush),
                    new Setter(Control.ForegroundProperty, Brushes.White)
                }
            });
            tabs.Resources.Add(typeof(ComboBoxItem), comboItemStyle);

            tabs.Resources[SystemColors.WindowBrushKey] = SurfaceBrush;
            tabs.Resources[SystemColors.WindowTextBrushKey] = Brushes.White;
            tabs.Resources[SystemColors.ControlBrushKey] = SurfaceBrush;
            tabs.Resources[SystemColors.ControlTextBrushKey] = Brushes.White;
            tabs.Resources[SystemColors.HighlightBrushKey] = AccentWashBrush;
            tabs.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
            tabs.Resources[SystemColors.WindowFrameBrushKey] = ThemeBorderBrush;

            Style checkBoxStyle = new Style(typeof(CheckBox));
            checkBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            checkBoxStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            tabs.Resources.Add(typeof(CheckBox), checkBoxStyle);

            Style listStyle = new Style(typeof(ListView));
            listStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            listStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBorderBrush));
            listStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            listStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            listStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            tabs.Resources.Add(typeof(ListView), listStyle);

            Style listItemStyle = new Style(typeof(ListViewItem));
            listItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            listItemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            listItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 4, 5, 4)));
            listItemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            listItemStyle.Triggers.Add(new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
                Setters = { new Setter(Control.BackgroundProperty, AccentWashBrush) }
            });
            listItemStyle.Triggers.Add(new Trigger
            {
                Property = ListViewItem.IsSelectedProperty,
                Value = true,
                Setters =
                {
                    new Setter(Control.BackgroundProperty, AccentWashBrush),
                    new Setter(Control.BorderBrushProperty, AccentBrush),
                    new Setter(Control.ForegroundProperty, Brushes.White)
                }
            });
            tabs.Resources.Add(typeof(ListViewItem), listItemStyle);

            Style columnHeaderStyle = new Style(typeof(GridViewColumnHeader));
            columnHeaderStyle.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBrush));
            columnHeaderStyle.Setters.Add(new Setter(Control.ForegroundProperty, AccentBrush));
            columnHeaderStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBorderBrush));
            columnHeaderStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            columnHeaderStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
            tabs.Resources.Add(typeof(GridViewColumnHeader), columnHeaderStyle);
        }

        private static void ApplyDropdownTheme(ComboBox combo, TabControl tabs)
        {
            if (combo == null || tabs == null) return;
            combo.Style = (Style)tabs.Resources[typeof(ComboBox)];
            combo.ItemContainerStyle = (Style)tabs.Resources[typeof(ComboBoxItem)];
            combo.Background = SurfaceBrush;
            combo.Foreground = Brushes.White;
            combo.BorderBrush = ThemeBorderBrush;
        }

        private void BuildControlsPage(TabControl tabs)
        {
            StackPanel page = new StackPanel
            {
                Margin = new Thickness(10, 14, 10, 0),
                Background = Brushes.Transparent
            };
            page.Children.Add(new TextBlock
            {
                Text = "FuelTarget controls",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AccentBrush,
                Margin = new Thickness(0, 0, 0, 8)
            });
            page.Children.Add(new TextBlock
            {
                Text = "Click each control to assign a keyboard key, wheel button, or other SimHub input. The actions adjust MMO_PlayerFuelTarget for the current car and track; the value is saved automatically.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 18)
            });

            page.Children.Add(new SimHub.Plugins.UI.ControlsEditor
            {
                ActionName = "MMOPlugin.FuelTargetIncrease",
                FriendlyName = "Increase FuelTarget (+0.02)",
                Margin = new Thickness(0, 4, 0, 6),
                FontSize = 12,
                Foreground = Brushes.White
            });
            page.Children.Add(new SimHub.Plugins.UI.ControlsEditor
            {
                ActionName = "MMOPlugin.FuelTargetDecrease",
                FriendlyName = "Decrease FuelTarget (-0.02)",
                Margin = new Thickness(0, 4, 0, 6),
                FontSize = 12,
                Foreground = Brushes.White
            });

            page.Children.Add(new TextBlock
            {
                Text = "The first adjustment starts from the saved average consumption for this car and track: VE for cars with Virtual Energy, otherwise fuel. FuelTarget cannot go below 0.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = InactiveTabBrush,
                FontSize = 11,
                Margin = new Thickness(0, 16, 0, 0)
            });

            tabs.Items.Add(new TabItem
            {
                Header = CreateTabHeader("Controls"),
                Content = page
            });
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
            _pluginUpdateFirstHint = new TextBlock
            {
                Text = "Update plugin first",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 12, 0),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Orange,
                Visibility = Visibility.Collapsed
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
            StackPanel statusText = new StackPanel();
            statusText.Children.Add(_overlayStatus);
            statusText.Children.Add(_pluginUpdateFirstHint);
            statusPanel.Children.Add(statusText);

            _overlayList = new ListView
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                FontSize = 12,
                Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
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

        private void BuildDataPage(TabControl tabs)
        {
            _dataStatus = new TextBlock
            {
                Text = "Saved statistics are based on completed laps.",
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };

            _dataCarFilter = new ComboBox { MinWidth = 190, Margin = new Thickness(0, 0, 14, 0) };
            _dataTrackFilter = new ComboBox { MinWidth = 190 };
            _dataCarFilter.SelectionChanged += (s, e) => { if (!_refreshingData) RefreshDataRows(); };
            _dataTrackFilter.SelectionChanged += (s, e) => { if (!_refreshingData) RefreshDataRows(); };

            Button backupButton = new Button
            {
                Content = "Backup",
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 78,
                Margin = new Thickness(7, 0, 0, 0),
                ToolTip = "Export your saved car and track statistics"
            };
            Button importButton = new Button
            {
                Content = "Import",
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 78,
                Margin = new Thickness(7, 0, 0, 0),
                ToolTip = "Restore statistics from a previous backup"
            };
            Button resetAllButton = new Button
            {
                Content = "Reset All",
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 88,
                Margin = new Thickness(7, 0, 0, 0),
                ToolTip = "Delete all saved car and track statistics"
            };
            backupButton.Click += BackupDataButton_Click;
            importButton.Click += ImportDataButton_Click;
            resetAllButton.Click += ResetAllDataButton_Click;

            StackPanel filters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            filters.Children.Add(new TextBlock { Text = "Car", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            filters.Children.Add(_dataCarFilter);
            filters.Children.Add(new TextBlock { Text = "Track", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            filters.Children.Add(_dataTrackFilter);
            filters.Children.Add(backupButton);
            filters.Children.Add(importButton);
            filters.Children.Add(resetAllButton);

            GridView view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "Car", Width = 175, DisplayMemberBinding = new Binding("Car") });
            view.Columns.Add(new GridViewColumn { Header = "Track", Width = 170, DisplayMemberBinding = new Binding("Track") });
            view.Columns.Add(new GridViewColumn { Header = "Completed laps", Width = 95, DisplayMemberBinding = new Binding("Laps") });
            FrameworkElementFactory bestLapText = new FrameworkElementFactory(typeof(TextBlock));
            bestLapText.SetBinding(TextBlock.TextProperty, new Binding("BestLap"));
            bestLapText.SetBinding(FrameworkElement.ToolTipProperty, new Binding("BestLapTimestamp"));
            DataTemplate bestLapTemplate = new DataTemplate { VisualTree = bestLapText };
            view.Columns.Add(new GridViewColumn { Header = "Best lap", Width = 100, CellTemplate = bestLapTemplate });
            view.Columns.Add(new GridViewColumn { Header = "Avg. fuel / lap", Width = 110, DisplayMemberBinding = new Binding("AverageFuel") });
            view.Columns.Add(new GridViewColumn { Header = "Avg. VE / lap", Width = 110, DisplayMemberBinding = new Binding("AverageVe") });
            _dataList = new ListView
            {
                View = view,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                FontSize = 12,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 10, 0, 0)
            };
            _dataList.PreviewMouseRightButtonDown += DataList_PreviewMouseRightButtonDown;
            ContextMenu rowMenu = new ContextMenu();
            MenuItem deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += DeleteSelectedDataRow_Click;
            rowMenu.Items.Add(deleteItem);
            _dataList.ContextMenu = rowMenu;
            VirtualizingPanel.SetIsVirtualizing(_dataList, true);

            Grid page = new Grid { Margin = new Thickness(12) };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.Children.Add(_dataStatus);
            Grid.SetRow(filters, 1);
            page.Children.Add(filters);
            Grid.SetRow(_dataList, 2);
            page.Children.Add(_dataList);

            _dataTab = new TabItem
            {
                Header = CreateTabHeader("Data"),
                Content = page
            };
            tabs.Items.Add(_dataTab);
            RefreshDataList();
        }

        private void BuildChangelogPage(TabControl tabs)
        {
            _changelogStatus = new TextBlock
            {
                Text = "Release notes will load from GitHub when you open this tab.",
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            _changelogEntries = new StackPanel();

            Button refreshButton = new Button
            {
                Content = "Refresh",
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 78,
                Margin = new Thickness(0, 0, 0, 10)
            };
            refreshButton.Click += async (s, e) => await LoadChangelogAsync(true);

            DockPanel header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(refreshButton, Dock.Right);
            header.Children.Add(refreshButton);
            header.Children.Add(_changelogStatus);

            ScrollViewer scroll = new ScrollViewer
            {
                Content = _changelogEntries,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            Grid page = new Grid { Margin = new Thickness(12) };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.Children.Add(header);
            Grid.SetRow(scroll, 1);
            page.Children.Add(scroll);

            _changelogTab = new TabItem
            {
                Header = CreateTabHeader("Changelog"),
                Content = page
            };
            tabs.Items.Add(_changelogTab);
        }

        private async Task LoadChangelogAsync(bool forceRefresh)
        {
            if (_loadingChangelog || (_changelogLoaded && !forceRefresh)) return;
            _loadingChangelog = true;
            _changelogStatus.Text = "Loading release notes from GitHub…";
            _changelogStatus.Foreground = Brushes.White;
            bool loadedFromCache = false;
            string refreshError = null;
            try
            {
                List<GitHubRelease> releases;
                try
                {
                    releases = await Task.Run(LoadAllReleases);
                    SaveChangelogCache(releases);
                }
                catch (Exception ex)
                {
                    refreshError = ex.Message;
                    releases = LoadChangelogCache();
                    loadedFromCache = releases.Count > 0;
                    if (!loadedFromCache)
                        throw;
                }

                _changelogEntries.Children.Clear();
                if (releases.Count == 0)
                {
                    _changelogStatus.Text = "No published releases were found.";
                    _changelogStatus.Foreground = InactiveTabBrush;
                    _changelogLoaded = true;
                    return;
                }

                foreach (GitHubRelease release in releases)
                {
                    string title = !string.IsNullOrWhiteSpace(release.Name)
                        ? release.Name.Trim()
                        : release.TagName ?? "Untitled release";
                    DateTime published;
                    string date = DateTime.TryParse(
                        release.PublishedAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out published)
                        ? published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
                        : "";

                    TextBlock heading = new TextBlock
                    {
                        Text = title + (string.IsNullOrEmpty(date) ? "" : "  ·  " + date),
                        Foreground = AccentBrush,
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    };
                    TextBlock notes = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(release.Body)
                            ? "No release notes provided."
                            : release.Body.Trim(),
                        Foreground = Brushes.White,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0)
                    };
                    Border card = new Border
                    {
                        BorderBrush = ThemeBorderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Background = SurfaceBrush,
                        Padding = new Thickness(10),
                        Margin = new Thickness(0, 0, 0, 9),
                        Child = new StackPanel
                        {
                            Children = { heading, notes }
                        }
                    };
                    _changelogEntries.Children.Add(card);
                }

                if (loadedFromCache)
                {
                    _changelogStatus.Text = "GitHub server could not be reached (" + refreshError +
                        "). Online check unavailable. Showing " +
                        releases.Count.ToString(CultureInfo.InvariantCulture) +
                        " cached release(s). Use Refresh to try again.";
                    _changelogStatus.Foreground = Brushes.Orange;
                }
                else
                {
                    _changelogStatus.Text = releases.Count.ToString(CultureInfo.InvariantCulture) +
                        " release(s) loaded from GitHub.";
                    _changelogStatus.Foreground = Brushes.White;
                }
                _changelogLoaded = true;
            }
            catch (Exception ex)
            {
                _changelogStatus.Text = "Could not load GitHub release notes: " + ex.Message;
                _changelogStatus.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                _loadingChangelog = false;
            }
        }

        private static string GetChangelogCachePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MMOPlugin",
                "changelog-cache.json"
            );
        }

        private static void SaveChangelogCache(List<GitHubRelease> releases)
        {
            if (releases == null || releases.Count == 0)
                return;

            try
            {
                string path = GetChangelogCachePath();
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temporaryPath = path + ".tmp";
                using (FileStream stream = File.Create(temporaryPath))
                {
                    DataContractJsonSerializer serializer =
                        new DataContractJsonSerializer(typeof(List<GitHubRelease>));
                    serializer.WriteObject(stream, releases);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporaryPath, path);
            }
            catch { }
        }

        private static List<GitHubRelease> LoadChangelogCache()
        {
            string path = GetChangelogCachePath();
            if (!File.Exists(path))
                return new List<GitHubRelease>();

            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    DataContractJsonSerializer serializer =
                        new DataContractJsonSerializer(typeof(List<GitHubRelease>));
                    List<GitHubRelease> releases =
                        serializer.ReadObject(stream) as List<GitHubRelease>;
                    return releases ?? new List<GitHubRelease>();
                }
            }
            catch
            {
                return new List<GitHubRelease>();
            }
        }

        private static void MergeChangelogCache(IEnumerable<GitHubRelease> releases)
        {
            if (releases == null) return;
            List<GitHubRelease> cached = LoadChangelogCache();
            foreach (GitHubRelease release in releases)
            {
                if (release == null || string.IsNullOrWhiteSpace(release.TagName)) continue;
                int existing = cached.FindIndex(item => item != null && string.Equals(
                    item.TagName, release.TagName, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) cached[existing] = release;
                else cached.Insert(0, release);
            }
            SaveChangelogCache(cached);
        }

        private static GitHubRelease GetLatestCachedRelease()
        {
            return LoadChangelogCache().FirstOrDefault(release =>
                release != null && !release.IsDraft && !release.IsPrerelease);
        }

        private void RefreshDataList()
        {
            if (_dataList == null || _statisticsStore == null) return;

            List<CarTrackStatisticsRecord> records;
            try { records = _statisticsStore.GetRecords(); }
            catch (Exception ex)
            {
                _dataStatus.Text = "Could not load saved statistics: " + ex.Message;
                _dataStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            string selectedCar = _dataCarFilter.SelectedItem as string;
            string selectedTrack = _dataTrackFilter.SelectedItem as string;
            _refreshingData = true;
            _dataCarFilter.Items.Clear();
            _dataTrackFilter.Items.Clear();
            _dataCarFilter.Items.Add("All cars");
            _dataTrackFilter.Items.Add("All tracks");
            List<CarTrackStatisticsRecord> visibleRecords = records
                .Where(item => item.CompletedLaps > 0)
                .ToList();
            foreach (string car in visibleRecords.Select(GetStatisticsDisplayName).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                _dataCarFilter.Items.Add(car);
            foreach (string track in visibleRecords.Select(item => item.TrackName).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                _dataTrackFilter.Items.Add(track);
            _dataCarFilter.SelectedItem = _dataCarFilter.Items.Contains(selectedCar) ? selectedCar : "All cars";
            _dataTrackFilter.SelectedItem = _dataTrackFilter.Items.Contains(selectedTrack) ? selectedTrack : "All tracks";
            _refreshingData = false;
            RefreshDataRows(visibleRecords);
        }

        private void RefreshDataRows()
        {
            if (_dataList == null || _statisticsStore == null) return;
            try { RefreshDataRows(_statisticsStore.GetRecords()); }
            catch (Exception ex)
            {
                _dataStatus.Text = "Could not load saved statistics: " + ex.Message;
                _dataStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private void RefreshDataRows(List<CarTrackStatisticsRecord> records)
        {
            if (_dataList == null) return;
            records = records.Where(item => item != null && item.CompletedLaps > 0).ToList();
            string car = _dataCarFilter.SelectedItem as string;
            string track = _dataTrackFilter.SelectedItem as string;
            IEnumerable<CarTrackStatisticsRecord> filtered = records;
            if (!string.IsNullOrEmpty(car) && car != "All cars")
                filtered = filtered.Where(item => string.Equals(GetStatisticsDisplayName(item), car, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(track) && track != "All tracks")
                filtered = filtered.Where(item => string.Equals(item.TrackName, track, StringComparison.OrdinalIgnoreCase));

            List<CarTrackStatisticsDisplayItem> rows = filtered.Select(item => new CarTrackStatisticsDisplayItem
            {
                Car = GetStatisticsDisplayName(item),
                Track = item.TrackName,
                CarKey = item.CarKey,
                TrackKey = item.TrackKey,
                Laps = item.CompletedLaps.ToString(CultureInfo.InvariantCulture),
                BestLap = FormatLapTime(item.BestLapSeconds),
                BestLapTimestamp = FormatTimestamp(item.BestLapTimestampUtc),
                AverageFuel = item.AverageFuel > 0.0 ? item.AverageFuel.ToString("0.00", CultureInfo.InvariantCulture) : "—",
                AverageVe = item.AverageVe > 0.0 ? item.AverageVe.ToString("0.00", CultureInfo.InvariantCulture) : "—"
            }).ToList();
            _dataList.ItemsSource = rows;
            _dataStatus.Foreground = Brushes.White;
            _dataStatus.Text = records.Count == 0
                ? "No saved lap data yet. Statistics are added as you complete laps."
                : records.Count.ToString(CultureInfo.InvariantCulture) +
                  " car and track combinations · consumption averages use complete laps without pit-lane time.";
            if (rows.Count == 0 && records.Count > 0)
                _dataStatus.Text += " No combinations match the selected filters.";
        }

        private static string GetStatisticsDisplayName(CarTrackStatisticsRecord record)
        {
            if (record == null) return "Unknown car";
            if (!string.IsNullOrWhiteSpace(record.VehicleModel))
                return MMOPlugin.FormatStatisticsVehicleName(record.Manufacturer, record.VehicleModel);
            if (!string.IsNullOrWhiteSpace(record.Manufacturer) &&
                !string.IsNullOrWhiteSpace(record.Model))
                return MMOPlugin.FormatStatisticsVehicleName(record.Manufacturer, record.Model);
            if (!string.IsNullOrWhiteSpace(record.Model))
                return record.Model.Trim();
            string keyModel = (record.CarKey ?? "").Split('|')[0].Trim();
            string fromKey = keyModel;
            int separator = Math.Max(fromKey.LastIndexOf('/'), fromKey.LastIndexOf('\\'));
            if (separator >= 0 && separator + 1 < fromKey.Length)
                fromKey = fromKey.Substring(separator + 1);
            int extension = fromKey.LastIndexOf('.');
            if (extension > 0) fromKey = fromKey.Substring(0, extension);
            if (fromKey.IndexOf("499P", StringComparison.OrdinalIgnoreCase) >= 0)
                fromKey = "Ferrari 499P";
            else
            {
                fromKey = Regex.Replace(fromKey, @"^\d+[_-]\d+[_-]", "");
                fromKey = Regex.Replace(fromKey, "[_-]+", " ").Trim();
            }
            if (!string.IsNullOrWhiteSpace(fromKey) &&
                !string.Equals(fromKey, "Unknown car", StringComparison.OrdinalIgnoreCase))
                return fromKey;
            return string.IsNullOrWhiteSpace(record.CarName) ? "Unknown car" : record.CarName;
        }

        private static string FormatLapTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0) return "—";
            TimeSpan value = TimeSpan.FromSeconds(seconds);
            return value.TotalHours >= 1.0
                ? value.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
                : value.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);
        }

        private void DataList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = e.OriginalSource as DependencyObject;
            ListViewItem row = ItemsControl.ContainerFromElement(_dataList, source) as ListViewItem;
            if (row != null) row.IsSelected = true;
        }

        private void DeleteSelectedDataRow_Click(object sender, RoutedEventArgs e)
        {
            CarTrackStatisticsDisplayItem selected = _dataList.SelectedItem as CarTrackStatisticsDisplayItem;
            if (selected == null) return;
            MessageBoxResult confirmation = MessageBox.Show(
                Window.GetWindow(this),
                "Delete the saved statistics for " + selected.Car + " on " + selected.Track + "?",
                "Delete statistics",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (confirmation != MessageBoxResult.Yes) return;
            try
            {
                _statisticsStore.CreateAutomaticBackup();
                _statisticsStore.DeleteRecord(selected.CarKey, selected.TrackKey);
                RefreshDataList();
                _dataStatus.Text = "Statistics deleted. An automatic backup was created.";
                _dataStatus.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                _dataStatus.Text = "Could not delete statistics: " + ex.Message;
                _dataStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private void ResetAllDataButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirmation = MessageBox.Show(
                Window.GetWindow(this),
                "Delete all saved car and track statistics? Race-start position snapshots will be kept. An automatic backup will be created first.",
                "Reset all statistics",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );
            if (confirmation != MessageBoxResult.Yes) return;
            try
            {
                _statisticsStore.CreateAutomaticBackup();
                _statisticsStore.DeleteAllRecords();
                RefreshDataList();
                _dataStatus.Text = "All car and track statistics were deleted. An automatic backup was created.";
                _dataStatus.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                _dataStatus.Text = "Could not reset statistics: " + ex.Message;
                _dataStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private static string FormatTimestamp(string utcTimestamp)
        {
            DateTime timestamp;
            if (!DateTime.TryParse(
                    utcTimestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out timestamp))
                return "";
            return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        }

        private void BackupDataButton_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "Back up MMO Plugin data",
                Filter = "MMO Plugin data backup (*.json)|*.json",
                FileName = "MMOPlugin-data-backup.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                _statisticsStore.ExportTo(dialog.FileName);
                _dataStatus.Text = "Backup saved to " + dialog.FileName;
                _dataStatus.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                _dataStatus.Text = "Could not create backup: " + ex.Message;
                _dataStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private void ImportDataButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Import MMO Plugin data backup",
                Filter = "MMO Plugin data backup (*.json)|*.json|JSON files (*.json)|*.json",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            MessageBoxResult confirmation = MessageBox.Show(
                Window.GetWindow(this),
                "Importing replaces the saved car and track statistics on this computer. Continue?",
                "Import MMO Plugin data",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (confirmation != MessageBoxResult.Yes) return;
            try
            {
                _statisticsStore.ImportFrom(dialog.FileName);
                RefreshDataList();
                _dataStatus.Text = "Data imported successfully.";
                _dataStatus.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                _dataStatus.Text = "Could not import data: " + ex.Message;
                _dataStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private static UIElement CreateUpdateTabHeader(string title, out Border updateBadge)
        {
            StackPanel header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock titleText = new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleText.SetBinding(
                TextBlock.ForegroundProperty,
                new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.FindAncestor,
                        typeof(TabItem),
                        1
                    )
                }
            );
            header.Children.Add(titleText);

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
            if (tabs != null && ReferenceEquals(e.OriginalSource, tabs))
            {
                UpdateRefreshTimerState();
                if (_settingsControlLoaded && IsHostWindowActive())
                    RefreshVisiblePage();
            }
            if (tabs != null && ReferenceEquals(e.OriginalSource, tabs) &&
                ReferenceEquals(tabs.SelectedItem, _settingsTab))
                SetTeamSpeakKeyVisible(false);
            if (tabs != null && ReferenceEquals(e.OriginalSource, tabs) &&
                ReferenceEquals(tabs.SelectedItem, _overlaysTab))
                await CheckOverlaysAsync();
            if (tabs != null && ReferenceEquals(e.OriginalSource, tabs) &&
                ReferenceEquals(tabs.SelectedItem, _dataTab))
                RefreshDataList();
            if (tabs != null && ReferenceEquals(e.OriginalSource, tabs) &&
                ReferenceEquals(tabs.SelectedItem, _changelogTab))
                await LoadChangelogAsync(false);
        }

        private void SetTeamSpeakKeyVisible(bool visible)
        {
            if (!visible) _showTeamSpeakKey.IsChecked = false;
            _teamSpeakKeyPassword.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            _teamSpeakKeyVisible.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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
                    Width = new GridLength(2.2, GridUnitType.Star)
                });
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1.8, GridUnitType.Star)
                });
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(100)
                });

                TextBlock name = new TextBlock
                {
                    Text = item.IsInstalled
                        ? item.InstalledDisplayName
                        : GetOverlayNameWithoutVersion(item.DisplayName) + " · Not installed",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 16, 0),
                    ToolTip = item.FileName
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                TextBlock status = new TextBlock
                {
                    Text = item.StatusText,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 12, 0),
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
            if (_pluginUpdateFirstHint != null)
            {
                _pluginUpdateFirstHint.Visibility =
                    _pluginUpdateStatusKnown && _pluginUpdateAvailable
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
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
            else if (ReferenceEquals(sender, _leaderboardPositionsGainedOption))
            {
                if (_setShowPositionsGained != null)
                    _setShowPositionsGained(_leaderboardPositionsGainedOption.IsChecked == true);
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

        private void PositionChangeColorCodingOption_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_setPositionChangeColorCodingDuration == null ||
                _positionChangeColorCodingOption.SelectedIndex < 0)
                return;

            int[] durationValues = { 0, 3, 5 };
            _setPositionChangeColorCodingDuration(
                durationValues[_positionChangeColorCodingOption.SelectedIndex]
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

        private void DebuggingOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_setDebuggingEnabled != null)
                _setDebuggingEnabled(_debuggingOption.IsChecked == true);
        }

        private void SearchBox_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            ApplyPropertyFilter();
            UpdateRefreshTimerState();
        }

        private void HideEmptyPropertiesOption_Changed(object sender, RoutedEventArgs e)
        {
            ApplyPropertyFilter();
        }

        private void ApplyPropertyFilter()
        {
            string query = (_searchBox.Text ?? "").Trim();
            int teamSpeakClientCount = string.IsNullOrEmpty(query)
                ? GetTeamSpeakClientCount()
                : 0;
            _displayedProperties.Clear();

            foreach (string name in _propertyNames)
            {
                string fullName = "MMOPlugin." + name;

                bool matches = string.IsNullOrEmpty(query)
                    ? IsDefaultProperty(name, teamSpeakClientCount)
                    : fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!matches)
                {
                    continue;
                }

                string value = GetCurrentValue(name);
                if (string.IsNullOrEmpty(query) && _hideEmptyPropertiesOption.IsChecked == true &&
                    !ShouldShowDefaultProperty(name, value, teamSpeakClientCount))
                    continue;

                _displayedProperties.Add(
                    new MMOPropertyDisplayItem
                    {
                        Name = fullName,
                        Value = value
                    }
                );
            }

            _resultCount.Text = _displayedProperties.Count.ToString(CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(query) ? " default properties" : " properties");
        }

        private int GetTeamSpeakClientCount()
        {
            int clientCount;
            return int.TryParse(
                GetCurrentValue("MMO_Teamspeak_ClientCount"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out clientCount)
                ? Math.Max(0, clientCount)
                : 0;
        }

        private static bool IsDefaultProperty(string propertyName, int teamSpeakClientCount)
        {
            Match teamSpeakMatch = Regex.Match(
                propertyName ?? "",
                @"^MMO_Teamspeak_(\d+)_",
                RegexOptions.IgnoreCase);
            if (teamSpeakMatch.Success)
            {
                int clientIndex;
                if (!int.TryParse(teamSpeakMatch.Groups[1].Value, out clientIndex))
                    return false;
                return clientIndex <= Math.Max(1, teamSpeakClientCount);
            }

            bool isPositionProperty = Regex.IsMatch(
                propertyName ?? "",
                @"^MMO_Position\d+",
                RegexOptions.IgnoreCase);
            if (!isPositionProperty)
                return true;

            return Regex.IsMatch(
                propertyName ?? "",
                @"^MMO_Position1(?:$|[^0-9])",
                RegexOptions.IgnoreCase);
        }

        private bool ShouldShowDefaultProperty(
            string propertyName,
            string value,
            int teamSpeakClientCount)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            double numericValue;
            if (!double.TryParse(
                    value,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out numericValue) || numericValue != 0)
                return true;

            if ((propertyName ?? "").StartsWith("MMO_Settings", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(propertyName, "MMO_Teamspeak_Connected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyName, "MMO_Teamspeak_ClientCount", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(propertyName, "MMO_Teamspeak_PlayerStatus", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyName, "MMO_Teamspeak_PlayerTalking", StringComparison.OrdinalIgnoreCase))
                return IsPositiveInteger(GetCurrentValue("MMO_Teamspeak_Connected"));

            Match teamSpeakMatch = Regex.Match(
                propertyName ?? "",
                @"^MMO_Teamspeak_(\d+)_(Status|Talking)$",
                RegexOptions.IgnoreCase);
            if (teamSpeakMatch.Success)
            {
                int clientIndex;
                return int.TryParse(teamSpeakMatch.Groups[1].Value, out clientIndex) &&
                    teamSpeakClientCount > 0 && clientIndex <= teamSpeakClientCount;
            }

            Match positionMatch = Regex.Match(
                propertyName ?? "",
                @"^MMO_Position(\d+)(.*)$",
                RegexOptions.IgnoreCase);
            if (positionMatch.Success)
            {
                int position;
                if (!int.TryParse(positionMatch.Groups[1].Value, out position) ||
                    !HasExistingCarAtPosition(position))
                    return false;
                return IsZeroCapableCarProperty(positionMatch.Groups[2].Value);
            }

            Match playerMatch = Regex.Match(
                propertyName ?? "",
                @"^MMO_Player(.+)$",
                RegexOptions.IgnoreCase);
            if (playerMatch.Success && HasExistingPlayerCar())
                return IsZeroCapableCarProperty(playerMatch.Groups[1].Value);

            return false;
        }

        private static bool IsPositiveInteger(string value)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
                parsed > 0;
        }

        private bool HasExistingCarAtPosition(int position)
        {
            double overall;
            return position > 0 && double.TryParse(
                GetCurrentValue("MMO_Position" + position.ToString(CultureInfo.InvariantCulture) + "Overall"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out overall) && overall > 0;
        }

        private bool HasExistingPlayerCar()
        {
            double overall;
            return double.TryParse(
                GetCurrentValue("MMO_PlayerOverall"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out overall) && overall > 0;
        }

        private static bool IsZeroCapableCarProperty(string suffix)
        {
            string[] zeroCapableSuffixes =
            {
                "PositionGain", "VirtualEnergy", "GapToLeader", "Penalties",
                "Inc", "Incidents", "InvalidLap", "FuelTarget", "VELap", "FuelLap",
                "VEAvg3Laps", "VELapsLeft", "FuelLapsLeft", "RefuelVE"
            };
            return zeroCapableSuffixes.Any(candidate =>
                string.Equals(suffix, candidate, StringComparison.OrdinalIgnoreCase));
        }

        private void RefreshCurrentValues()
        {
            if (string.IsNullOrWhiteSpace(_searchBox.Text) &&
                _hideEmptyPropertiesOption.IsChecked == true)
            {
                RefreshFilteredDefaultProperties();
                return;
            }

            foreach (MMOPropertyDisplayItem item in _displayedProperties)
            {
                string propertyName = item.Name.Substring(
                    "MMOPlugin.".Length
                );
                item.Value = GetCurrentValue(propertyName);
            }
        }

        private void RefreshFilteredDefaultProperties()
        {
            int teamSpeakClientCount = GetTeamSpeakClientCount();
            foreach (string propertyName in _propertyNames)
            {
                if (!IsDefaultProperty(propertyName, teamSpeakClientCount))
                    continue;

                string fullName = "MMOPlugin." + propertyName;
                string value = GetCurrentValue(propertyName);
                int displayedIndex = -1;
                for (int i = 0; i < _displayedProperties.Count; i++)
                {
                    if (string.Equals(_displayedProperties[i].Name, fullName, StringComparison.Ordinal))
                    {
                        displayedIndex = i;
                        break;
                    }
                }

                bool shouldShow = ShouldShowDefaultProperty(
                    propertyName,
                    value,
                    teamSpeakClientCount);
                if (!shouldShow)
                {
                    if (displayedIndex >= 0)
                        _displayedProperties.RemoveAt(displayedIndex);
                    continue;
                }

                if (displayedIndex >= 0)
                {
                    _displayedProperties[displayedIndex].Value = value;
                    continue;
                }

                int sourceIndex = _propertyNames.IndexOf(propertyName);
                int insertIndex = _displayedProperties.Count;
                for (int i = 0; i < _displayedProperties.Count; i++)
                {
                    string displayedName = _displayedProperties[i].Name.Substring("MMOPlugin.".Length);
                    if (_propertyNames.IndexOf(displayedName) > sourceIndex)
                    {
                        insertIndex = i;
                        break;
                    }
                }
                _displayedProperties.Insert(insertIndex, new MMOPropertyDisplayItem
                {
                    Name = fullName,
                    Value = value
                });
            }

            _resultCount.Text = _displayedProperties.Count.ToString(CultureInfo.InvariantCulture) +
                " default properties";
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
            _settingsControlLoaded = true;
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.Activated += HostWindow_Activated;
                _hostWindow.Deactivated += HostWindow_Deactivated;
            }
            UpdateRefreshTimerState();
            if (IsHostWindowActive())
                RefreshVisiblePage();
            if (!_checkingUpdate && _latestRelease == null)
                CheckForUpdatesAsync();
        }

        private void SettingsControl_Unloaded(
            object sender,
            RoutedEventArgs e)
        {
            _settingsControlLoaded = false;
            _refreshTimer.Stop();
            if (_hostWindow != null)
            {
                _hostWindow.Activated -= HostWindow_Activated;
                _hostWindow.Deactivated -= HostWindow_Deactivated;
                _hostWindow = null;
            }
        }

        private void HostWindow_Activated(object sender, EventArgs e)
        {
            UpdateRefreshTimerState();
            RefreshVisiblePage();
        }

        private void HostWindow_Deactivated(object sender, EventArgs e)
        {
            _refreshTimer.Stop();
        }

        private bool IsHostWindowActive()
        {
            return _hostWindow == null || _hostWindow.IsActive;
        }

        private void UpdateRefreshTimerState()
        {
            bool pluginPageSelected = _pluginTab != null && _pluginTab.IsSelected;
            bool visibleLivePage = pluginPageSelected ||
                (_dataTab != null && _dataTab.IsSelected);
            _refreshTimer.Interval = TimeSpan.FromSeconds(10);
            if (_settingsControlLoaded && IsHostWindowActive() && visibleLivePage)
                _refreshTimer.Start();
            else
                _refreshTimer.Stop();
        }

        private void RefreshVisiblePage()
        {
            if (_pluginTab != null && _pluginTab.IsSelected)
                RefreshCurrentValues();
            else if (_dataTab != null && _dataTab.IsSelected)
                RefreshDataList();
        }

        private void RefreshTimer_Tick(
            object sender,
            EventArgs e)
        {
            if (!_settingsControlLoaded || !IsHostWindowActive())
            {
                _refreshTimer.Stop();
                return;
            }

            RefreshVisiblePage();
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
                _overlayStatus.Text = "Finding the newest release that contains an overlay ZIP…";
                OverlayReleaseSelection selection = await Task.Run(FindLatestOverlayReleaseAsset);
                if (selection == null || selection.Asset == null)
                {
                    _overlayItems.Clear();
                    RefreshOverlayList();
                    _overlayStatus.Text = "No release contains an MMO overlay ZIP asset.";
                    return;
                }

                GitHubReleaseAsset asset = selection.Asset;
                bool usedOlderRelease = selection.IsFallbackRelease;

                Version githubVersion;
                if (!Version.TryParse(ExtractOverlayVersion(asset.Name), out githubVersion))
                    throw new InvalidDataException(
                        "The overlay ZIP name does not contain a usable version number: " + asset.Name
                    );

                string cacheRoot = GetOverlayCacheDirectory();
                Directory.CreateDirectory(cacheRoot);
                string archivePath = Path.Combine(cacheRoot, Path.GetFileName(asset.Name));
                CleanupOverlayCache(cacheRoot, archivePath);
                CleanupOverlayUpdaterTempDirectory();
                bool downloadPackage = !IsCachedOverlayAssetCurrent(asset, archivePath);
                if (!downloadPackage)
                    SaveOverlayCacheManifest(asset, selection.Release.TagName, cacheRoot);
                _overlayStatus.Text = downloadPackage
                    ? "Downloading " + (usedOlderRelease ? "latest available " : "latest ") +
                        "overlay package v" + githubVersion + " from GitHub…"
                    : "Latest overlay package v" + githubVersion + " is already cached. Checking installed overlays…";

                List<OverlayPackageItem> items = await Task.Run(
                    () => DownloadAndInspectOverlayPackage(
                        asset,
                        archivePath,
                        downloadPackage,
                        selection.Release.TagName
                    )
                );
                _overlayItems.Clear();
                _overlayItems.AddRange(items);
                _overlayPackageVersion = ExtractOverlayVersion(asset.Name) ?? "latest";
                RefreshOverlayList();
                _overlayStatus.Text = (selection.IsCachedFallback
                        ? "GitHub server could not be reached. Online check unavailable; using cached package. "
                        : downloadPackage ? "Downloaded " : "Checked cached ") +
                    (usedOlderRelease ? "latest available " : "latest ") + "overlay package v" +
                    _overlayPackageVersion + ". " +
                    items.Count.ToString(CultureInfo.InvariantCulture) +
                    " overlay(s) from release " + selection.Release.TagName +
                    " compared with installed versions.";
                if (selection.IsCachedFallback)
                    _overlayStatus.Foreground = Brushes.OrangeRed;
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
                if (!IsOverlayPackageAssetName(name) ||
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

        private static bool IsOverlayPackageAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return false;

            return name.IndexOf("Overlays", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static OverlayReleaseSelection FindLatestOverlayReleaseAsset()
        {
            try
            {
                return FindLatestOverlayReleaseAssetFromGitHub();
            }
            catch (WebException)
            {
                return FindCachedOverlayReleaseAsset();
            }
        }

        private static OverlayReleaseSelection FindLatestOverlayReleaseAssetFromGitHub()
        {
            const int pageSize = 100;
            const int maximumPages = 10;
            GitHubRelease latestStableRelease = LoadLatestRelease();
            for (int page = 1; page <= maximumPages; page++)
            {
                GitHubRelease[] releases = LoadReleasePage(page);
                if (releases == null || releases.Length == 0) break;
                MergeChangelogCache(releases);
                foreach (GitHubRelease release in releases)
                {
                    // The overlay package can be published on a prerelease or a later
                    // release whose ZIP version is unchanged. Select by release order,
                    // not by the package version embedded in the ZIP filename.
                    if (release == null || release.IsDraft) continue;
                    GitHubReleaseAsset asset = FindOverlayAsset(release);
                    if (asset != null)
                    {
                        return new OverlayReleaseSelection
                        {
                            Release = release,
                            Asset = asset,
                            IsFallbackRelease = latestStableRelease == null ||
                                !string.Equals(
                                    release.TagName,
                                    latestStableRelease.TagName,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        };
                    }
                }
                if (releases.Length < pageSize) break;
            }
            return null;
        }

        private static OverlayReleaseSelection FindCachedOverlayReleaseAsset()
        {
            string cacheRoot = GetOverlayCacheDirectory();
            string manifestPath = GetOverlayCacheManifestPath(cacheRoot);
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException(
                    "GitHub returned 403 and no previously downloaded overlay package is cached.");

            try
            {
                OverlayCacheManifest manifest;
                using (FileStream stream = File.OpenRead(manifestPath))
                {
                    DataContractJsonSerializer serializer =
                        new DataContractJsonSerializer(typeof(OverlayCacheManifest));
                    manifest = (OverlayCacheManifest)serializer.ReadObject(stream);
                }
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Name))
                    throw new InvalidDataException("The cached overlay package information is invalid.");

                string archivePath = Path.Combine(cacheRoot, manifest.Name);
                if (!File.Exists(archivePath) || !ZipHasDashboard(archivePath))
                    throw new InvalidDataException("The last downloaded overlay package is missing or invalid.");

                GitHubReleaseAsset asset = new GitHubReleaseAsset
                {
                    Name = manifest.Name,
                    DownloadUrl = manifest.DownloadUrl,
                    UpdatedAt = manifest.UpdatedAt,
                    Size = manifest.Size
                };
                return new OverlayReleaseSelection
                {
                    Release = new GitHubRelease
                    {
                        TagName = string.IsNullOrWhiteSpace(manifest.ReleaseTag)
                            ? "cached"
                            : manifest.ReleaseTag,
                        Assets = new[] { asset }
                    },
                    Asset = asset,
                    IsFallbackRelease = false,
                    IsCachedFallback = true
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "GitHub returned 403 and the cached overlay package could not be used: " + ex.Message,
                    ex);
            }
        }

        private static GitHubRelease[] LoadReleasePage(int page)
        {
            string url = "https://api.github.com/repos/St54Kevin/MMOPlugin/releases?per_page=100&page=" +
                page.ToString(CultureInfo.InvariantCulture);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
            request.Accept = "application/vnd.github+json";
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(
                System.Net.Cache.RequestCacheLevel.Reload
            );
            request.Timeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubRelease[]));
                return (GitHubRelease[])serializer.ReadObject(stream);
            }
        }

        private static List<GitHubRelease> LoadAllReleases()
        {
            const int pageSize = 100;
            const int maximumPages = 100;
            List<GitHubRelease> releases = new List<GitHubRelease>();
            for (int page = 1; page <= maximumPages; page++)
            {
                GitHubRelease[] pageReleases = LoadReleasePage(page);
                if (pageReleases == null || pageReleases.Length == 0) break;
                releases.AddRange(pageReleases.Where(release => release != null && !release.IsDraft));
                if (pageReleases.Length < pageSize) break;
            }
            return releases;
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

        private static string GetOverlayCacheManifestPath(string cacheRoot)
        {
            return Path.Combine(cacheRoot, "current-package.json");
        }

        private static bool IsCachedOverlayAssetCurrent(GitHubReleaseAsset asset, string archivePath)
        {
            if (asset == null || !File.Exists(archivePath))
                return false;

            try
            {
                string cacheRoot = Path.GetDirectoryName(archivePath);
                string manifestPath = GetOverlayCacheManifestPath(cacheRoot);
                if (!File.Exists(manifestPath))
                    return false;

                OverlayCacheManifest manifest;
                using (FileStream stream = File.OpenRead(manifestPath))
                {
                    DataContractJsonSerializer serializer =
                        new DataContractJsonSerializer(typeof(OverlayCacheManifest));
                    manifest = (OverlayCacheManifest)serializer.ReadObject(stream);
                }

                FileInfo archive = new FileInfo(archivePath);
                return manifest != null &&
                    string.Equals(manifest.Name, asset.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(manifest.DownloadUrl, asset.DownloadUrl, StringComparison.Ordinal) &&
                    string.Equals(manifest.UpdatedAt, asset.UpdatedAt, StringComparison.Ordinal) &&
                    manifest.Size == asset.Size &&
                    (asset.Size <= 0 || archive.Length == asset.Size) &&
                    ZipHasDashboard(archivePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool ZipHasDashboard(string archivePath)
        {
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                return archive.Entries.Any(entry => entry.FullName.EndsWith(
                    ".simhubdash", StringComparison.OrdinalIgnoreCase));
        }

        private static void SaveOverlayCacheManifest(
            GitHubReleaseAsset asset,
            string releaseTag,
            string cacheRoot)
        {
            OverlayCacheManifest manifest = new OverlayCacheManifest
            {
                Name = asset.Name,
                DownloadUrl = asset.DownloadUrl,
                UpdatedAt = asset.UpdatedAt,
                Size = asset.Size,
                ReleaseTag = releaseTag
            };
            string path = GetOverlayCacheManifestPath(cacheRoot);
            string temporaryPath = path + ".tmp";
            using (FileStream stream = File.Create(temporaryPath))
            {
                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(OverlayCacheManifest));
                serializer.WriteObject(stream, manifest);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporaryPath, path);
        }

        private static void CleanupOverlayCache(string cacheRoot, string currentArchivePath)
        {
            string currentName = Path.GetFileName(currentArchivePath);
            string currentContents = Path.GetFileNameWithoutExtension(currentArchivePath) + "_contents";
            foreach (string file in Directory.GetFiles(cacheRoot))
            {
                string name = Path.GetFileName(file);
                if (!string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "current-package.json", StringComparison.OrdinalIgnoreCase))
                    TryDeleteFile(file);
                if (name.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
                    TryDeleteFile(file);
            }
            foreach (string directory in Directory.GetDirectories(cacheRoot))
            {
                if (!string.Equals(Path.GetFileName(directory), currentContents, StringComparison.OrdinalIgnoreCase))
                    TryDeleteDirectory(directory);
            }
        }

        private static void CleanupOverlayUpdaterTempDirectory()
        {
            string workDirectory = Path.Combine(Path.GetTempPath(), "MMOPluginOverlays");
            if (!Directory.Exists(workDirectory)) return;

            string backupRoot = Path.Combine(workDirectory, "backups");
            TryDeleteDirectory(backupRoot);
            foreach (string directory in Directory.GetDirectories(workDirectory, "unpack-*"))
                TryDeleteDirectory(directory);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }

        private static List<OverlayPackageItem> DownloadAndInspectOverlayPackage(
            GitHubReleaseAsset asset,
            string archivePath,
            bool downloadPackage,
            string releaseTag)
        {
            if (asset == null || string.IsNullOrEmpty(archivePath))
                throw new InvalidDataException("The release overlay archive has no download URL.");

            string cacheRoot = Path.GetDirectoryName(archivePath);
            Directory.CreateDirectory(cacheRoot);
            // Download only when the caller found that the cached GitHub asset changed.
            if (downloadPackage && !string.IsNullOrEmpty(asset.DownloadUrl))
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
                    SaveOverlayCacheManifest(asset, releaseTag, cacheRoot);
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
            scriptText.AppendLine("Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue");
            scriptText.AppendLine("Get-ChildItem -LiteralPath (Join-Path $env:TEMP 'MMOPluginOverlays') -Directory -Filter 'unpack-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue");
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
                    if (release.IsCached)
                    {
                        _updateStatus.Text = "GitHub server could not be reached. Online check unavailable. " +
                            "Using fallback release data: " + _updateStatus.Text;
                        _updateStatus.Foreground = Brushes.OrangeRed;
                    }
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
                    if (release.IsCached)
                    {
                        _updateStatus.Text = "GitHub server could not be reached. Online check unavailable. " +
                            "Using fallback release data: " + _updateStatus.Text;
                        _updateStatus.Foreground = Brushes.OrangeRed;
                    }
                    else
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
            try
            {
                GitHubRelease release = LoadLatestReleaseFromGitHub();
                if (release != null) MergeChangelogCache(new[] { release });
                return release;
            }
            catch (WebException)
            {
                GitHubRelease cached = GetLatestCachedRelease();
                if (cached != null)
                {
                    cached.IsCached = true;
                    return cached;
                }

                GitHubRelease publicPageRelease = LoadLatestReleaseFromPublicPage();
                publicPageRelease.IsCached = true;
                return publicPageRelease;
            }
        }

        private static GitHubRelease LoadLatestReleaseFromPublicPage()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                "https://github.com/St54Kevin/MMOPlugin/releases/latest");
            request.UserAgent = "Mozilla/5.0 MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
            request.AllowAutoRedirect = true;
            request.Timeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                Match tagMatch = Regex.Match(
                    response.ResponseUri.AbsolutePath,
                    @"/releases/tag/([^/]+)$",
                    RegexOptions.IgnoreCase);
                if (!tagMatch.Success)
                    throw new InvalidDataException("GitHub did not redirect to a latest release tag.");

                string tag = Uri.UnescapeDataString(tagMatch.Groups[1].Value);
                return new GitHubRelease
                {
                    TagName = tag,
                    IsCached = true,
                    Assets = new[]
                    {
                        new GitHubReleaseAsset
                        {
                            Name = "MMOPlugin.dll",
                            DownloadUrl = "https://github.com/St54Kevin/MMOPlugin/releases/download/" +
                                Uri.EscapeDataString(tag) + "/MMOPlugin.dll"
                        }
                    }
                };
            }
        }

        private static GitHubRelease LoadLatestReleaseFromGitHub()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
            request.UserAgent = "MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
            request.Accept = "application/vnd.github+json";
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(
                System.Net.Cache.RequestCacheLevel.Reload
            );
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

                _statisticsStore.CreateAutomaticBackup();
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
        [IgnoreDataMember]
        public bool IsCached { get; set; }
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }
        [DataMember(Name = "name")]
        public string Name { get; set; }
        [DataMember(Name = "assets")]
        public GitHubReleaseAsset[] Assets { get; set; }
        [DataMember(Name = "draft")]
        public bool IsDraft { get; set; }
        [DataMember(Name = "prerelease")]
        public bool IsPrerelease { get; set; }
        [DataMember(Name = "body")]
        public string Body { get; set; }
        [DataMember(Name = "published_at")]
        public string PublishedAt { get; set; }
        [DataMember(Name = "html_url")]
        public string HtmlUrl { get; set; }
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

    internal sealed class OverlayReleaseSelection
    {
        public GitHubRelease Release { get; set; }
        public GitHubReleaseAsset Asset { get; set; }
        public bool IsFallbackRelease { get; set; }
        public bool IsCachedFallback { get; set; }
    }

    [DataContract]
    internal sealed class OverlayCacheManifest
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }
        [DataMember(Name = "downloadUrl")]
        public string DownloadUrl { get; set; }
        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
        [DataMember(Name = "size")]
        public long Size { get; set; }
        [DataMember(Name = "releaseTag")]
        public string ReleaseTag { get; set; }
    }

    internal sealed class CarTrackStatisticsDisplayItem
    {
        public string Car { get; set; }
        public string Track { get; set; }
        public string CarKey { get; set; }
        public string TrackKey { get; set; }
        public string Laps { get; set; }
        public string BestLap { get; set; }
        public string BestLapTimestamp { get; set; }
        public string AverageFuel { get; set; }
        public string AverageVe { get; set; }
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
