using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameReaderCommon;
using SimHub.Plugins;

namespace MMOPlugin
{
    [PluginName("MMO Plugin")]
    [PluginAuthor("Kevin")]
    [PluginDescription("Extended Le Mans Ultimate standings data.")]
    public class MMOPlugin : IPlugin, IDataPlugin
    {
        public PluginManager PluginManager { get; set; }

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

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;

            _standingsApi.Start();

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
                    capturedPosition.ToString("00");

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
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.LastLapTime
                            : 0.0;
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

                        return FormatGap(
                            vehicle.GapToLeader
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

                        return FormatGap(
                            vehicle.IntervalToNext
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
                        "FinishStatus",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.FinishStatus ?? ""
                            : "";
                    }
                );
            }
        }

        public void DataUpdate(
            PluginManager pluginManager,
            ref GameData data)
        {
            _sharedMemory.UpdateConnection();

            if (!_sharedMemory.IsConnected)
            {
                ClearRelativeGaps();
                return;
            }

            _hasScoringInfo =
                _sharedMemory.TryReadScoringInfo(
                    out _scoringInfo
                );

            if (!_hasScoringInfo)
            {
                ClearRelativeGaps();
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

            for (int telemetryIndex = 0;
                 telemetryIndex < 104;
                 telemetryIndex++)
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
                    telemetryById.Add(
                        telemetry.mID,
                        telemetry
                    );
                }
            }

            List<LMUVehicleScoring> vehicles =
                new List<LMUVehicleScoring>();

            LMUVehicleScoring player =
                new LMUVehicleScoring();

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

            List<RelativeVehicle> ahead =
                new List<RelativeVehicle>();

            List<RelativeVehicle> behind =
                new List<RelativeVehicle>();

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

                double distanceAhead =
                    vehicle.mLapDist -
                    player.mLapDist;

                if (distanceAhead < 0.0)
                    distanceAhead += trackLength;

                double distanceBehind =
                    player.mLapDist -
                    vehicle.mLapDist;

                if (distanceBehind < 0.0)
                    distanceBehind += trackLength;

                if (distanceAhead > 0.5)
                {
                    ahead.Add(
                        new RelativeVehicle
                        {
                            Scoring = vehicle,
                            Distance = distanceAhead
                        }
                    );
                }

                if (distanceBehind > 0.5)
                {
                    behind.Add(
                        new RelativeVehicle
                        {
                            Scoring = vehicle,
                            Distance = distanceBehind
                        }
                    );
                }
            }

            ahead.Sort(
                (a, b) =>
                    a.Distance.CompareTo(b.Distance)
            );

            behind.Sort(
                (a, b) =>
                    a.Distance.CompareTo(b.Distance)
            );

            LMUVehicleTelemetryRelative playerTelemetry;

            if (!telemetryById.TryGetValue(
                    player.mID,
                    out playerTelemetry))
            {
                return;
            }

            /*
             * AHEAD
             *
             * LMU kann mTimeGapCarAhead mit negativem
             * Vorzeichen liefern.
             *
             * Intern verwenden wir den Betrag und geben
             * den fertigen relativen Abstand anschließend
             * explizit negativ aus.
             *
             * Ergebnis:
             *   -1.2
             *   -2.7
             *   -4.1
             */

            double cumulativeGap =
                GetValidRelativeGap(
                    Math.Abs(
                        playerTelemetry.mTimeGapCarAhead
                    )
                );

            for (int i = 0;
                 i < 3 &&
                 i < ahead.Count;
                 i++)
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

                    double nextGap =
                        GetValidRelativeGap(
                            Math.Abs(
                                previousAheadTelemetry
                                    .mTimeGapCarAhead
                            )
                        );

                    if (double.IsNaN(nextGap))
                        break;

                    cumulativeGap += nextGap;
                }

                if (double.IsNaN(cumulativeGap) ||
                    cumulativeGap > 180.0)
                {
                    break;
                }

                _relativeAhead[i] =
                    FormatRelativeGap(
                        -cumulativeGap
                    );
            }

            /*
             * BEHIND
             *
             * Behind bleibt positiv.
             *
             * Ergebnis:
             *   +1.5
             *   +3.2
             *   +5.8
             */

            cumulativeGap =
                GetValidRelativeGap(
                    playerTelemetry.mTimeGapCarBehind
                );

            for (int i = 0;
                 i < 3 &&
                 i < behind.Count;
                 i++)
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

                    double nextGap =
                        GetValidRelativeGap(
                            previousBehindTelemetry
                                .mTimeGapCarBehind
                        );

                    if (double.IsNaN(nextGap))
                        break;

                    cumulativeGap += nextGap;
                }

                if (double.IsNaN(cumulativeGap) ||
                    cumulativeGap > 180.0)
                {
                    break;
                }

                _relativeBehind[i] =
                    FormatRelativeGap(
                        cumulativeGap
                    );
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

        private static double GetValidRelativeGap(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value <= 0.0 ||
                value > 180.0)
            {
                return double.NaN;
            }

            return value;
        }

        private static string FormatRelativeGap(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                Math.Abs(value) > 180.0)
            {
                return "";
            }

            /*
             * Explizites Vorzeichen:
             *
             *   -1.2
             *   +1.2
             *    0.0
             */

            return value.ToString(
                "+0.0;-0.0;0.0",
                CultureInfo.InvariantCulture
            );
        }

        private sealed class RelativeVehicle
        {
            public LMUVehicleScoring Scoring;
            public double Distance;
        }

        private string FormatGap(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0.0)
            {
                return "";
            }

            int session =
                _hasScoringInfo
                    ? _scoringInfo.mSession
                    : 0;

            bool isRace =
                session >= 10 &&
                session <= 13;

            return value.ToString(
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
}