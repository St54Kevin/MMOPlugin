using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace MMOPlugin
{
    public sealed class LMUStandingsApi : IDisposable
    {
        private const string StandingsUrl =
            "http://127.0.0.1:6397/rest/watch/standings";

        private const int MaxVehicles = 62;

        private readonly object _lock =
            new object();

        private Timer _timer;

        private LMUApiVehicle[] _vehicles =
            new LMUApiVehicle[MaxVehicles];

        private bool _isConnected;

        private string _lastError = "";

        private int _session;

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _isConnected;
                }
            }
        }

        public string LastError
        {
            get
            {
                lock (_lock)
                {
                    return _lastError;
                }
            }
        }

        public void Start()
        {
            if (_timer != null)
                return;

            _timer = new Timer(
                Update,
                null,
                0,
                250
            );
        }

        public void SetSession(int session)
        {
            lock (_lock)
            {
                _session = session;
            }
        }

        public LMUApiVehicle GetVehicleByPosition(
            int position)
        {
            if (position < 1 ||
                position > MaxVehicles)
            {
                return null;
            }

            lock (_lock)
            {
                return _vehicles[position - 1];
            }
        }

        private void Update(object state)
        {
            try
            {
                string json;

                using (WebClient client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;

                    json = client.DownloadString(
                        StandingsUrl
                    );
                }

                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(
                        typeof(LMUApiVehicle[])
                    );

                using (MemoryStream stream =
                    new MemoryStream(
                        Encoding.UTF8.GetBytes(json)))
                {
                    LMUApiVehicle[] vehicles =
                        (LMUApiVehicle[])serializer.ReadObject(
                            stream
                        );

                    UpdateVehicles(vehicles);
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _isConnected = false;

                    _lastError =
                        ex.GetType().Name +
                        ": " +
                        ex.Message;
                }
            }
        }

        private void UpdateVehicles(
            LMUApiVehicle[] source)
        {
            LMUApiVehicle[] newVehicles =
                new LMUApiVehicle[MaxVehicles];

            if (source != null)
            {
                foreach (LMUApiVehicle vehicle in source)
                {
                    if (vehicle == null)
                        continue;

                    int position =
                        vehicle.Position;

                    if (position < 1 ||
                        position > MaxVehicles)
                    {
                        continue;
                    }

                    newVehicles[position - 1] =
                        vehicle;
                }
            }

            int session;

            lock (_lock)
            {
                session = _session;
            }

            bool isPracticeOrQualifying =
                IsPracticeOrQualifying(session);

            if (isPracticeOrQualifying)
            {
                CalculatePracticeQualifyingData(
                    newVehicles
                );
            }
            else
            {
                CalculateRaceClassPositions(
                    newVehicles
                );
            }

            lock (_lock)
            {
                _vehicles = newVehicles;

                _isConnected = true;

                _lastError = "";
            }
        }

        private static void CalculatePracticeQualifyingData(
            LMUApiVehicle[] vehicles)
        {
            Dictionary<string, List<LMUApiVehicle>>
                vehiclesByClass =
                    new Dictionary<string, List<LMUApiVehicle>>(
                        StringComparer.OrdinalIgnoreCase
                    );

            for (int i = 0; i < vehicles.Length; i++)
            {
                LMUApiVehicle vehicle =
                    vehicles[i];

                if (vehicle == null)
                    continue;

                if (!IsValidLapTime(
                        vehicle.BestLapTime))
                {
                    vehicle.PositionClass = 0;
                    continue;
                }

                string carClass =
                    vehicle.CarClass ?? "";

                List<LMUApiVehicle> classVehicles;

                if (!vehiclesByClass.TryGetValue(
                        carClass,
                        out classVehicles))
                {
                    classVehicles =
                        new List<LMUApiVehicle>();

                    vehiclesByClass.Add(
                        carClass,
                        classVehicles
                    );
                }

                classVehicles.Add(vehicle);
            }

            foreach (
                KeyValuePair<
                    string,
                    List<LMUApiVehicle>> pair
                in vehiclesByClass)
            {
                List<LMUApiVehicle> classVehicles =
                    pair.Value;

                classVehicles.Sort(
                    CompareBestLapTime
                );

                for (int i = 0;
                     i < classVehicles.Count;
                     i++)
                {
                    LMUApiVehicle vehicle =
                        classVehicles[i];

                    vehicle.PositionClass =
                        i + 1;

                    double classLeaderBestLap =
                        classVehicles[0].BestLapTime;

                    vehicle.PracticeQualifyingGap =
                        vehicle.BestLapTime -
                        classLeaderBestLap;

                    if (i > 0)
                    {
                        double nextFasterBestLap =
                            classVehicles[i - 1].BestLapTime;

                        vehicle.PracticeQualifyingInterval =
                            vehicle.BestLapTime -
                            nextFasterBestLap;
                    }
                    else
                    {
                        vehicle.PracticeQualifyingInterval =
                            0.0;
                    }

                    if (vehicle.PracticeQualifyingGap < 0.0)
                    {
                        vehicle.PracticeQualifyingGap =
                            0.0;
                    }

                    if (vehicle.PracticeQualifyingInterval < 0.0)
                    {
                        vehicle.PracticeQualifyingInterval =
                            0.0;
                    }
                }
            }
        }

        private static void CalculateRaceClassPositions(
            LMUApiVehicle[] vehicles)
        {
            Dictionary<string, int> classPositions =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase
                );

            for (int i = 0;
                 i < vehicles.Length;
                 i++)
            {
                LMUApiVehicle vehicle =
                    vehicles[i];

                if (vehicle == null)
                    continue;

                string carClass =
                    vehicle.CarClass ?? "";

                int classPosition;

                if (!classPositions.TryGetValue(
                        carClass,
                        out classPosition))
                {
                    classPosition = 0;
                }

                classPosition++;

                classPositions[carClass] =
                    classPosition;

                vehicle.PositionClass =
                    classPosition;
            }
        }

        private static int CompareBestLapTime(
            LMUApiVehicle a,
            LMUApiVehicle b)
        {
            return a.BestLapTime.CompareTo(
                b.BestLapTime
            );
        }

        private static bool IsPracticeOrQualifying(
            int session)
        {
            /*
             * LMU/rFactor 2 session values:
             *
             * 0      Test Day
             * 1-4    Practice
             * 5-8    Qualifying
             * 9      Warmup
             * 10-13  Race
             */

            return
                (session >= 1 && session <= 8);
        }

        private static bool IsValidLapTime(
            double value)
        {
            return
                !double.IsNaN(value) &&
                !double.IsInfinity(value) &&
                value > 0.0;
        }

        public void Dispose()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }
    }

    [DataContract]
    public sealed class LMUApiVehicle
    {
        [DataMember(Name = "position")]
        public int Position { get; set; }

        [DataMember(Name = "carNumber")]
        public string CarNumber { get; set; }

        [DataMember(Name = "carClass")]
        public string CarClass { get; set; }

        [DataMember(Name = "driverName")]
        public string DriverName { get; set; }

        [DataMember(Name = "fullTeamName")]
        public string TeamName { get; set; }

        [DataMember(Name = "bestLapTime")]
        public double BestLapTime { get; set; }

        [DataMember(Name = "lastLapTime")]
        public double LastLapTime { get; set; }

        [DataMember(Name = "timeBehindLeader")]
        public double TimeBehindLeader { get; set; }

        [DataMember(Name = "timeBehindNext")]
        public double TimeBehindNext { get; set; }

        [DataMember(Name = "timeBehindClassLeader")]
        public double TimeBehindClassLeader { get; set; }

        [DataMember(Name = "pitState")]
        public string PitState { get; set; }

        [DataMember(Name = "pitting")]
        public bool Pitting { get; set; }

        [DataMember(Name = "inGarageStall")]
        public bool InGarageStall { get; set; }

        [DataMember(Name = "veFraction")]
        public double VeFraction { get; set; }

        [DataMember(Name = "fuelFraction")]
        public double FuelFraction { get; set; }

        [DataMember(Name = "penalties")]
        public int Penalties { get; set; }

        [DataMember(Name = "finishStatus")]
        public string FinishStatus { get; set; }

        public int PositionClass { get; set; }

        public double PracticeQualifyingGap { get; set; }

        public double PracticeQualifyingInterval { get; set; }

        public double GapToLeader
        {
            get
            {
                /*
                 * Practice / Qualifying:
                 * gap to fastest car in own class.
                 *
                 * Race:
                 * gap to class leader from LMU API.
                 */
                if (PracticeQualifyingGap >= 0.0)
                    return PracticeQualifyingGap;

                return TimeBehindClassLeader;
            }
        }

        public double IntervalToNext
        {
            get
            {
                /*
                 * Practice / Qualifying:
                 * difference to next faster car
                 * in the same class.
                 *
                 * Race:
                 * LMU API timeBehindNext.
                 */
                if (PracticeQualifyingInterval >= 0.0)
                    return PracticeQualifyingInterval;

                return TimeBehindNext;
            }
        }
    }
}