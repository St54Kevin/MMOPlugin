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

        private const int ExpectedScoringInfoSize = 548;

        private const int ExpectedVehicleScoringSize = 584;

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
                    Marshal.SizeOf(typeof(LMUScoringInfo));

                int vehicleScoringSize =
                    Marshal.SizeOf(typeof(LMUVehicleScoring));

                if (scoringInfoSize != ExpectedScoringInfoSize ||
                    vehicleScoringSize != ExpectedVehicleScoringSize)
                {
                    throw new InvalidOperationException(
                        "LMU structure size mismatch. " +
                        "ScoringInfo=" + scoringInfoSize +
                        ", VehicleScoring=" + vehicleScoringSize
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

        private T ReadStruct<T>(long offset)
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

        private void LogReadError(Exception ex)
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
}