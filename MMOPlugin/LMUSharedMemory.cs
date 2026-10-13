using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace MMOPlugin
{
    public sealed class LMUSharedMemory : IDisposable
    {
        private const string SharedMemoryName = "LMU_Data";

        private const long ScoringOffset = 1632;

        private const int ScoringStreamSize = 12;

        private const int MaxVehicles = 62;

        private const int MaxMappedVehicles = 104;

        private const int ResultsStreamSize = 65536;

        private const int TelemetryHeaderSize = 4;

        private const int ExpectedScoringInfoSize = 548;

        private const int ExpectedVehicleScoringSize = 584;

        private const int ExpectedVehicleTelemetryRelativeSize = 1888;

        private const int ExpectedTimeGapCarAheadOffset = 780;

        private const int ExpectedTimeGapCarBehindOffset = 784;

        private MemoryMappedFile _memoryMappedFile;

        private MemoryMappedViewAccessor _viewAccessor;

        private DateTime _nextConnectionAttempt =
            DateTime.MinValue;

        private string _lastError = "";

        public bool IsConnected
        {
            get
            {
                return _memoryMappedFile != null &&
                       _viewAccessor != null;
            }
        }

        public string LastError
        {
            get
            {
                return _lastError;
            }
        }

        public void UpdateConnection()
        {
            if (IsConnected)
                return;

            if (DateTime.UtcNow < _nextConnectionAttempt)
                return;

            _nextConnectionAttempt =
                DateTime.UtcNow.AddSeconds(1);

            try
            {
                _memoryMappedFile =
                    MemoryMappedFile.OpenExisting(
                        SharedMemoryName,
                        MemoryMappedFileRights.Read
                    );

                _viewAccessor =
                    _memoryMappedFile.CreateViewAccessor(
                        0,
                        0,
                        MemoryMappedFileAccess.Read
                    );

                int scoringInfoSize =
                    Marshal.SizeOf(
                        typeof(LMUScoringInfo)
                    );

                int vehicleScoringSize =
                    Marshal.SizeOf(
                        typeof(LMUVehicleScoring)
                    );

                int telemetrySize =
                    Marshal.SizeOf(
                        typeof(LMUVehicleTelemetryRelative)
                    );

                int timeGapAheadOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTimeGapCarAhead"
                    ).ToInt32();

                int timeGapBehindOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTimeGapCarBehind"
                    ).ToInt32();

                if (scoringInfoSize !=
                        ExpectedScoringInfoSize ||
                    vehicleScoringSize !=
                        ExpectedVehicleScoringSize ||
                    telemetrySize !=
                        ExpectedVehicleTelemetryRelativeSize ||
                    timeGapAheadOffset !=
                        ExpectedTimeGapCarAheadOffset ||
                    timeGapBehindOffset !=
                        ExpectedTimeGapCarBehindOffset)
                {
                    throw new InvalidOperationException(
                        "LMU structure size/offset mismatch. " +
                        "ScoringInfo=" +
                        scoringInfoSize +
                        ", VehicleScoring=" +
                        vehicleScoringSize +
                        ", Telemetry=" +
                        telemetrySize +
                        ", GapAheadOffset=" +
                        timeGapAheadOffset +
                        ", GapBehindOffset=" +
                        timeGapBehindOffset
                    );
                }

                _lastError = "";
            }
            catch (Exception ex)
            {
                CloseHandles();

                _lastError =
                    ex.GetType().Name +
                    ": " +
                    ex.Message;
            }
        }

        public bool TryReadScoringInfo(
            out LMUScoringInfo info)
        {
            info = new LMUScoringInfo();

            if (!IsConnected)
                return false;

            try
            {
                info =
                    ReadStruct<LMUScoringInfo>(
                        ScoringOffset
                    );

                return true;
            }
            catch (Exception ex)
            {
                LogReadError(ex);
                return false;
            }
        }

        public bool TryReadVehicleScoring(
            int vehicleIndex,
            out LMUVehicleScoring vehicle)
        {
            vehicle = new LMUVehicleScoring();

            if (!IsConnected ||
                vehicleIndex < 0 ||
                vehicleIndex >= MaxVehicles)
            {
                return false;
            }

            try
            {
                long vehicleOffset =
                    ScoringOffset
                    + Marshal.SizeOf(
                        typeof(LMUScoringInfo)
                    )
                    + ScoringStreamSize
                    + ((long)vehicleIndex *
                       Marshal.SizeOf(
                           typeof(LMUVehicleScoring)
                       ));

                vehicle =
                    ReadStruct<LMUVehicleScoring>(
                        vehicleOffset
                    );

                return true;
            }
            catch (Exception ex)
            {
                LogReadError(ex);
                return false;
            }
        }

        public bool TryReadVehicleTelemetryRelative(
            int telemetryIndex,
            out LMUVehicleTelemetryRelative telemetry)
        {
            telemetry =
                new LMUVehicleTelemetryRelative();

            if (!IsConnected ||
                telemetryIndex < 0 ||
                telemetryIndex >= MaxMappedVehicles)
            {
                return false;
            }

            try
            {
                long telemetryOffset =
                    ScoringOffset
                    + ExpectedScoringInfoSize
                    + ScoringStreamSize
                    + ((long)MaxMappedVehicles *
                       ExpectedVehicleScoringSize)
                    + ResultsStreamSize
                    + TelemetryHeaderSize
                    + ((long)telemetryIndex *
                       ExpectedVehicleTelemetryRelativeSize);

                telemetry =
                    ReadStruct<LMUVehicleTelemetryRelative>(
                        telemetryOffset
                    );

                return true;
            }
            catch (Exception ex)
            {
                LogReadError(ex);
                return false;
            }
        }

        private T ReadStruct<T>(
            long offset)
            where T : struct
        {
            int size =
                Marshal.SizeOf(typeof(T));

            byte[] buffer =
                new byte[size];

            int bytesRead =
                _viewAccessor.ReadArray(
                    offset,
                    buffer,
                    0,
                    size
                );

            if (bytesRead != size)
            {
                throw new InvalidOperationException(
                    "Incomplete memory read: " +
                    bytesRead +
                    " of " +
                    size +
                    " bytes."
                );
            }

            IntPtr pointer =
                Marshal.AllocHGlobal(size);

            try
            {
                Marshal.Copy(
                    buffer,
                    0,
                    pointer,
                    size
                );

                return (T)Marshal.PtrToStructure(
                    pointer,
                    typeof(T)
                );
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private void LogReadError(
            Exception ex)
        {
            _lastError =
                ex.GetType().Name +
                ": " +
                ex.Message;
        }

        private void CloseHandles()
        {
            if (_viewAccessor != null)
            {
                _viewAccessor.Dispose();
                _viewAccessor = null;
            }

            if (_memoryMappedFile != null)
            {
                _memoryMappedFile.Dispose();
                _memoryMappedFile = null;
            }
        }

        public void Dispose()
        {
            CloseHandles();
        }
    }

    [StructLayout(
        LayoutKind.Explicit,
        Pack = 4,
        Size = 1888)]
    public struct LMUVehicleTelemetryRelative
    {
        [FieldOffset(0)]
        public int mID;

        [FieldOffset(780)]
        public float mTimeGapCarAhead;

        [FieldOffset(784)]
        public float mTimeGapCarBehind;

        [FieldOffset(788)]
        public float mTimeGapPlaceAhead;

        [FieldOffset(792)]
        public float mTimeGapPlaceBehind;
    }
}