using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Fleck;
using DPUruNet;

namespace U4500_LocalBridge
{
    class Program
    {
        private const int Port = 8182;
        private const int MaxImageDimension = 4096;

        static IWebSocketConnection? currentSocket;
        static IWebSocketConnection? captureSocket;
        static Reader? currentReader;
        static Form hiddenForm;
        static bool captureInProgress;

        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("   Local Bridge U.are.U 4500 (SIM TNI AD)   ");
            Console.WriteLine("==============================================");
            Console.WriteLine("Memulai server...");

            hiddenForm = new Form();
            _ = hiddenForm.Handle;

            var server = new WebSocketServer($"ws://0.0.0.0:{Port}");
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    Console.WriteLine("🌐 Web Client terkoneksi!");
                    currentSocket = socket;
                };

                socket.OnClose = () =>
                {
                    Console.WriteLine("❌ Web Client terputus!");

                    if (ReferenceEquals(currentSocket, socket))
                    {
                        currentSocket = null;
                    }

                    if (ReferenceEquals(captureSocket, socket))
                    {
                        captureSocket = null;
                        captureInProgress = false;
                    }

                    CancelReaderCapture();
                };

                socket.OnMessage = message =>
                {
                    Console.WriteLine($"📩 Pesan diterima dari Web: {message}");

                    if (message == "CHECK_STATUS")
                    {
                        hiddenForm.BeginInvoke((MethodInvoker)(() => CheckDeviceStatus(socket)));
                    }
                    else if (message == "CAPTURE_FINGER")
                    {
                        hiddenForm.BeginInvoke((MethodInvoker)(() => StartCapture(socket)));
                    }
                };
            });

            Console.WriteLine($"✅ WebSocket Server berjalan di ws://localhost:{Port}");
            Console.WriteLine("⏳ Jangan tutup jendela ini selama aplikasi SIM dipakai.");
            Console.WriteLine("Tekan Ctrl+C atau tutup jendela ini untuk keluar...");

            Application.Run();

            CancelReaderCapture();
            currentReader?.Dispose();
        }

        static void CheckDeviceStatus(IWebSocketConnection socket)
        {
            try
            {
                ReaderCollection readers = ReaderCollection.GetReaders();

                if (readers.Count == 0)
                {
                    currentReader = null;
                    SafeSend(socket, "STATUS:ERROR|Scanner U.are.U 4500 tidak terdeteksi di USB");
                    Console.WriteLine("⚠️ Device tidak ditemukan.");
                    return;
                }

                currentReader = readers[0];
                SafeSend(socket, "STATUS:READY");
                Console.WriteLine($"🔍 Device ditemukan: {currentReader.Description.Name}");
            }
            catch (Exception ex)
            {
                SafeSend(socket, $"STATUS:ERROR|{ex.Message}");
                Console.WriteLine($"❌ CheckDeviceStatus: {ex.Message}");
            }
        }

        static void StartCapture(IWebSocketConnection socket)
        {
            if (captureInProgress)
            {
                SafeSend(socket, "ERROR:Scanner sedang memproses scan sebelumnya");
                return;
            }

            if (currentReader == null)
            {
                SafeSend(socket, "ERROR:Device belum siap");
                Console.WriteLine("⚠️ StartCapture gagal: currentReader null.");
                return;
            }

            Console.WriteLine("🔄 Memulai proses StartCapture...");

            try
            {
                CancelReaderCapture();

                Constants.ResultCode openResult =
                    currentReader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);

                if (openResult != Constants.ResultCode.DP_SUCCESS)
                {
                    string err = $"ERROR:Gagal membuka scanner. Kode: {openResult}";
                    SafeSend(socket, err);
                    Console.WriteLine($"❌ {err}");
                    return;
                }

                currentReader.On_Captured -= new Reader.CaptureCallback(OnCaptured);
                currentReader.On_Captured += new Reader.CaptureCallback(OnCaptured);

                captureSocket = socket;
                captureInProgress = true;

                SafeSend(socket, "INFO:SILAKAN TEMPELKAN JARI KE ALAT");
                Console.WriteLine("💡 Lampu scanner menyala. Menunggu jari ditempel...");

                Constants.ResultCode captureResult = currentReader.CaptureAsync(
                    Constants.Formats.Fid.ANSI,
                    Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
                    currentReader.Capabilities.Resolutions[0]);

                if (captureResult == Constants.ResultCode.DP_SUCCESS)
                {
                    Console.WriteLine("✅ CaptureAsync berjalan! Menunggu hasil capture...");
                }
                else
                {
                    captureInProgress = false;
                    captureSocket = null;

                    string err = $"ERROR:Gagal memulai scan async. Kode: {captureResult}";
                    SafeSend(socket, err);
                    Console.WriteLine($"❌ {err}");
                }
            }
            catch (Exception ex)
            {
                captureInProgress = false;
                captureSocket = null;

                string err = $"ERROR:{ex.Message}";
                SafeSend(socket, err);
                Console.WriteLine($"❌ Exception di StartCapture: {ex.Message}\n{ex.StackTrace}");
            }
        }

        static void OnCaptured(CaptureResult captureResult)
        {
            var socket = captureSocket ?? currentSocket;

            if (socket == null)
            {
                captureInProgress = false;
                captureSocket = null;
                Console.WriteLine("⚠️ OnCaptured: tidak ada WebSocket aktif.");
                return;
            }

            try
            {
                Console.WriteLine($"📥 Capture diterima. Quality={captureResult.Quality}");

                if (captureResult.Data?.Views == null || captureResult.Data.Views.Count == 0)
                {
                    SendCaptureError(socket, $"Data/Views kosong (Quality={captureResult.Quality})");
                    return;
                }

                // Jangan membuang frame hanya karena flag quality bukan GOOD.
                // Gambar tetap dikirim ke browser agar operator dapat melihat hasil
                // capture dan memutuskan apakah perlu melakukan scan ulang.
                SendInfo(socket, $"CAPTURE QUALITY: {captureResult.Quality}");

                foreach (var view in captureResult.Data.Views)
                {
                    try
                    {
                        Console.WriteLine(
                            $"  View {view.Width}x{view.Height}, RawImage={view.RawImage?.Length ?? 0} bytes");

                        using Bitmap bmp = CreateBitmap(view.RawImage, view.Width, view.Height);
                        string base64 = ConvertBitmapToBase64(bmp);

                        Console.WriteLine($"  Bitmap berhasil dibuat. Base64={base64.Length} chars");
                        SafeSend(socket, $"IMAGE:data:image/png;base64,{base64}");
                        Console.WriteLine("✅ Gambar sidik jari berhasil dikirim.");

                        captureInProgress = false;
                        captureSocket = null;
                        BeginCancelCapture();
                        return;
                    }
                    catch (Exception viewEx)
                    {
                        Console.WriteLine($"⚠️ View gagal diproses: {viewEx.Message}");
                    }
                }

                SendCaptureError(socket, "Tidak ada view sidik jari yang dapat dikonversi menjadi gambar.");
            }
            catch (Exception ex)
            {
                SendCaptureError(socket, $"Gagal memproses gambar: {ex.Message}");
                Console.WriteLine($"❌ OnCaptured: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                captureInProgress = false;
                captureSocket = null;
            }
        }

        static Bitmap CreateBitmap(byte[]? bytes, int width, int height)
        {
            if (bytes == null || bytes.Length == 0)
                throw new InvalidOperationException("RawImage kosong.");

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"Dimensi gambar tidak valid: {width}x{height}.");

            if (width > MaxImageDimension || height > MaxImageDimension)
                throw new InvalidOperationException($"Dimensi gambar terlalu besar: {width}x{height}.");

            long pixelCountLong = (long)width * height;
            if (pixelCountLong > int.MaxValue / 3)
                throw new InvalidOperationException("Ukuran gambar terlalu besar untuk diproses.");

            int pixelCount = (int)pixelCountLong;
            var grayscale = new byte[pixelCount];

            int copyLength = Math.Min(bytes.Length, grayscale.Length);
            Buffer.BlockCopy(bytes, 0, grayscale, 0, copyLength);

            if (copyLength < grayscale.Length)
            {
                Array.Fill(grayscale, (byte)255, copyLength, grayscale.Length - copyLength);
                Console.WriteLine(
                    $"⚠️ RawImage lebih kecil dari ukuran piksel yang diharapkan: {bytes.Length} < {grayscale.Length}.");
            }

            var rgbBytes = new byte[pixelCount * 3];
            for (int i = 0; i < grayscale.Length; i++)
            {
                byte value = grayscale[i];
                int offset = i * 3;
                rgbBytes[offset] = value;
                rgbBytes[offset + 1] = value;
                rgbBytes[offset + 2] = value;
            }

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData? locked = null;

            try
            {
                locked = bmp.LockBits(
                    new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);

                int rowBytes = width * 3;

                for (int y = 0; y < height; y++)
                {
                    IntPtr rowPtr = IntPtr.Add(locked.Scan0, y * locked.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(
                        rgbBytes,
                        y * rowBytes,
                        rowPtr,
                        rowBytes);
                }
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
            finally
            {
                if (locked != null)
                    bmp.UnlockBits(locked);
            }

            return bmp;
        }

        static string ConvertBitmapToBase64(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }

        static void BeginCancelCapture()
        {
            try
            {
                hiddenForm.BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        currentReader?.CancelCapture();
                        Console.WriteLine("  🛑 Capture dibatalkan setelah hasil diterima.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ CancelCapture: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ BeginInvoke CancelCapture: {ex.Message}");
            }
        }

        static void CancelReaderCapture()
        {
            try
            {
                currentReader?.CancelCapture();
            }
            catch
            {
                // Scanner mungkin sudah idle / tidak sedang capture.
            }

            captureInProgress = false;
            captureSocket = null;
        }

        static void SendInfo(IWebSocketConnection socket, string message) =>
            SafeSend(socket, $"INFO:{message}");

        static void SendCaptureError(IWebSocketConnection socket, string message)
        {
            SafeSend(socket, $"ERROR:{message}");
            captureInProgress = false;
            captureSocket = null;
        }

        static void SafeSend(IWebSocketConnection socket, string message)
        {
            try
            {
                socket.Send(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ WebSocket send gagal: {ex.Message}");
            }
        }
    }
}
