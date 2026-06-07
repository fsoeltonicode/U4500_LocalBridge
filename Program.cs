using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Fleck;
using DPUruNet;

namespace U4500_LocalBridge
{
    class Program
    {
        static IWebSocketConnection? currentSocket;
        static Reader? currentReader;
        static Form hiddenForm;

        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("   Local Bridge U.are.U 4500 (SIM TNI AD)   ");
            Console.WriteLine("==============================================");
            Console.WriteLine("Memulai server...");

            hiddenForm = new Form();
            IntPtr handle = hiddenForm.Handle; // Memaksa pembuatan HWND agar message loop berjalan

            var server = new WebSocketServer("ws://0.0.0.0:8182");
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
                    currentSocket = null;
                    if (currentReader != null) {
                        try { 
                            hiddenForm.BeginInvoke((MethodInvoker)delegate {
                                try { currentReader.CancelCapture(); } catch { }
                            });
                        } catch { }
                    }
                };
                socket.OnMessage = message =>
                {
                    Console.WriteLine($"📩 Pesan diterima dari Web: {message}");
                    if (message == "CHECK_STATUS")
                    {
                        hiddenForm.BeginInvoke((MethodInvoker)delegate {
                            CheckDeviceStatus(socket);
                        });
                    }
                    else if (message == "CAPTURE_FINGER")
                    {
                        hiddenForm.BeginInvoke((MethodInvoker)delegate {
                            StartCapture(socket);
                        });
                    }
                };
            });

            Console.WriteLine("✅ WebSocket Server berjalan di ws://localhost:8182");
            Console.WriteLine("⏳ Jangan tutup jendela ini selama aplikasi SIM dipakai.");
            Console.WriteLine("Tekan Ctrl+C atau tutup jendela ini untuk keluar...");
            
            Application.Run();

            if (currentReader != null)
            {
                currentReader.Dispose();
            }
        }

        static void CheckDeviceStatus(IWebSocketConnection socket)
        {
            try 
            {
                ReaderCollection readers = ReaderCollection.GetReaders();
                if (readers.Count > 0)
                {
                    currentReader = readers[0];
                    socket.Send("STATUS:READY");
                    Console.WriteLine($"🔍 Device ditemukan: {currentReader.Description.Name}");
                }
                else
                {
                    socket.Send("STATUS:ERROR|Scanner U.are.U 4500 tidak terdeteksi di USB");
                    Console.WriteLine("⚠️ Device tidak ditemukan.");
                }
            }
            catch (Exception ex)
            {
                socket.Send($"STATUS:ERROR|{ex.Message}");
            }
        }

        static void StartCapture(IWebSocketConnection socket)
        {
            if (currentReader == null)
            {
                socket.Send("ERROR:Device belum siap");
                Console.WriteLine("⚠️ StartCapture gagal: currentReader null.");
                return;
            }

            Console.WriteLine("🔄 Memulai proses StartCapture...");
            try
            {
                try { 
                    currentReader.CancelCapture(); 
                } catch { }

                Constants.ResultCode result = currentReader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);
                if (result != Constants.ResultCode.DP_SUCCESS && result != Constants.ResultCode.DP_DEVICE_BUSY)
                {
                    string err = $"ERROR:Gagal membuka port scanner. Kode: {result}";
                    socket.Send(err);
                    Console.WriteLine($"❌ {err}");
                    return;
                }

                currentReader.On_Captured -= new Reader.CaptureCallback(OnCaptured);
                currentReader.On_Captured += new Reader.CaptureCallback(OnCaptured);

                socket.Send("INFO:SILAKAN TEMPELKAN JARI KE ALAT");
                Console.WriteLine("💡 Lampu scanner menyala. Menunggu jari ditempel...");

                Constants.ResultCode captureResult = currentReader.CaptureAsync(Constants.Formats.Fid.ANSI, Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT, currentReader.Capabilities.Resolutions[0]);
                
                if (captureResult == Constants.ResultCode.DP_SUCCESS)
                {
                    Console.WriteLine("✅ CaptureAsync berjalan! Menunggu interupsi USB...");
                }
                else
                {
                    string err = $"ERROR:Gagal memulai scan async. Kode: {captureResult}";
                    socket.Send(err);
                    Console.WriteLine($"❌ {err}");
                }
            }
            catch (Exception ex)
            {
                string err = $"ERROR:{ex.Message}";
                socket.Send(err);
                Console.WriteLine($"❌ Exception di StartCapture: {ex.Message}\n{ex.StackTrace}");
            }
        }

        static void OnCaptured(CaptureResult captureResult)
        {
            if (currentSocket == null) 
            {
                Console.WriteLine("⚠️ OnCaptured: currentSocket null, batal mengirim.");
                return;
            }

            if (captureResult.Quality != Constants.CaptureQuality.DP_QUALITY_GOOD)
            {
                string err = $"ERROR:Kualitas tangkapan buruk atau dibatalkan (Quality={captureResult.Quality})";
                currentSocket.Send(err);
                Console.WriteLine($"❌ {err}");
                return;
            }

            if (captureResult.Data != null && captureResult.Data.Views != null && captureResult.Data.Views.Count > 0)
            {
                try
                {
                    Console.WriteLine($"  Jumlah view sidik jari: {captureResult.Data.Views.Count}");
                    foreach (var view in captureResult.Data.Views)
                    {
                        Console.WriteLine($"  Membuat Bitmap dari ukuran {view.Width}x{view.Height}...");
                        Bitmap bmp = CreateBitmap(view.RawImage, view.Width, view.Height);
                        
                        Console.WriteLine("  Mengubah Bitmap ke Base64 (PNG)...");
                        string base64 = ConvertBitmapToBase64(bmp);
                        
                        Console.WriteLine("  Mengirim Base64 ke browser via WebSocket...");
                        currentSocket.Send($"IMAGE:data:image/png;base64,{base64}");
                        Console.WriteLine("✅ Gambar sidik jari berhasil dikirim.");
                        break; 
                    }
                    
                    hiddenForm.BeginInvoke((MethodInvoker)delegate {
                        try {
                            Console.WriteLine("  Mematikan lampu scanner...");
                            currentReader?.CancelCapture();
                        } catch { }
                    });
                }
                catch (Exception ex)
                {
                    string err = $"ERROR:Gagal memproses gambar: {ex.Message}";
                    currentSocket.Send(err);
                    Console.WriteLine($"❌ {err}\n{ex.StackTrace}");
                }
            }
            else
            {
                string err = $"ERROR:Gagal menangkap sidik jari (Data/Views null)";
                currentSocket.Send(err);
                Console.WriteLine($"❌ {err}");
            }
        }

        static Bitmap CreateBitmap(byte[] bytes, int width, int height)
        {
            byte[] rgbBytes = new byte[bytes.Length * 3];
            for (int i = 0; i < bytes.Length; i++)
            {
                rgbBytes[(i * 3)] = bytes[i];
                rgbBytes[(i * 3) + 1] = bytes[i];
                rgbBytes[(i * 3) + 2] = bytes[i];
            }
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            for (int i = 0; i < bmp.Height; i++)
            {
                IntPtr p = new IntPtr(data.Scan0.ToInt64() + data.Stride * i);
                System.Runtime.InteropServices.Marshal.Copy(rgbBytes, i * bmp.Width * 3, p, bmp.Width * 3);
            }

            bmp.UnlockBits(data);

            return bmp;
        }

        static string ConvertBitmapToBase64(Bitmap bitmap)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                byte[] byteImage = ms.ToArray();
                return Convert.ToBase64String(byteImage);
            }
        }
    }
}
