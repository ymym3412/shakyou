using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.AI.MachineLearning.Preview;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;
using Windows.UI.Text;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.System.Threading;
using System.Threading;
using System.Diagnostics;
using Windows.Media.Devices;
using Windows.Devices.Enumeration;

// 空白ページの項目テンプレートについては、https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x411 を参照してください

namespace Shakyo_TinyYOLO
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private const string MODEL_FILENAME = "TinyYOLO.onnx";

        private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);
        private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);
        private readonly double lineThickness = 2.0;

        private MediaCapture _captureManager;
        private VideoEncodingProperties _videoProperties;
        private ThreadPoolTimer _frameProcessingTimer;
        // リソースプールへアクセスできるスレッド数を1に制限する
        private SemaphoreSlim _frameProcessingSemaphore = new SemaphoreSlim(1);

        //model系の変数
        private ImageVariableDescriptorPreview inputImageDescription;
        private TensorVariableDescriptorPreview outputTensorDescription;
        private LearningModelPreview model = null;

        //Bounding Boxの情報を保持しておくリスト
        private IList<YoloBoundingBox> boxes = new List<YoloBoundingBox>();
        private YoloWinMLParser parser = new YoloWinMLParser();


        public MainPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await LoadModelAsync();
        }

        // 画像のボタンを押した場合の処理を記述
        // 画像を扱う場合の処理
        private async void ButtonRun_Click(object sender, RoutedEventArgs e)
        {

            ButtonRun.IsEnabled = false;

            try
            {
                // Load Model
                await Task.Run(async () => await LoadModelAsync());

                // Trigger file picker to select an image file
                var fileOpenPicker = new FileOpenPicker();
                fileOpenPicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                fileOpenPicker.FileTypeFilter.Add(".jpg");
                fileOpenPicker.FileTypeFilter.Add(".png");
                fileOpenPicker.ViewMode = PickerViewMode.Thumbnail;
                var selectedStorageFile = await fileOpenPicker.PickSingleFileAsync();

                SoftwareBitmap softwareBitmap;

                // 画像のBitmap表現を取得する？
                using (IRandomAccessStream stream = await selectedStorageFile.OpenAsync(FileAccessMode.Read))
                {
                    // Create the decoder from the stream
                    var decoder = await BitmapDecoder.CreateAsync(stream);

                    //Get the softwareBitmap representation of the file in BGRA8 format
                    softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                    softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }

                // Encapsulate the image within a VideoFrame to be bount and evaluated
                var inputImage = VideoFrame.CreateWithSoftwareBitmap(softwareBitmap);

                await Task.Run(async () =>
                {
                    // 入力画像をモデルに通してBBを計算
                    await EvaluateVideoFrameAsync(inputImage);
                });

                await DrawOverlays(inputImage);
            }
            catch (Exception ex)
            {
                Debug.Write("7");
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"error: {ex.Message}");
                ButtonRun.IsEnabled = true;
            }
        }

        // BBを付与した画像を表示する
        private async Task DrawOverlays(VideoFrame inputImage)
        {
           this.OverlayCanvas.Children.Clear();

            // Render output
            if (this.boxes.Count > 0)
            {
                // Remove overlapping and low confidence bounding boxes
                var filterdBoxes = this.parser.NonMaxSuppress(this.boxes, 1, .5F);

                foreach (var box in filterdBoxes)
                {
                    await this.DrawYoloBoundingBoxAsync(inputImage.SoftwareBitmap, box);
                }
            }
        }

        // Webcamera向けのボタンを押したときの処理
        private async void OnWebCameraButtonClicked(object sender, RoutedEventArgs e)
        {
            if (_captureManager == null || _captureManager.CameraStreamState != CameraStreamState.Streaming)
            {
                await StartWebCameraAsync();
            }
            else
            {
                await StopWebCameraAsync();
            }
        }

        private async void OnDeviceToggleToggled(object sender, RoutedEventArgs e)
        {
            await LoadModelAsync(DeviceToggle.IsOn);
        }


        // webcameraのスタート
        private async Task StartWebCameraAsync()
        {
            try
            {
                if (_captureManager == null ||
                    _captureManager.CameraStreamState == CameraStreamState.Shutdown ||
                    _captureManager.CameraStreamState == CameraStreamState.NotStreaming)
                {
                    if (_captureManager != null)
                    {
                        // captureManagerのリソースを解放する
                        _captureManager.Dispose();
                    }

                    // LifeCam優先で、それがなければ取得した全カメラIDのリストから先頭のものを使用する
                    MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
                    var allCameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                    var selectedCamera = allCameras.FirstOrDefault(c => c.Name.Contains("LifeCam")) ?? allCameras.FirstOrDefault();
                    if (selectedCamera != null)
                    {
                        settings.VideoDeviceId = selectedCamera.Id;
                    }

                    _captureManager = new MediaCapture();
                    await _captureManager.InitializeAsync(settings);

                    WebCamCaptureElement.Source = _captureManager;
                }

                if (_captureManager.CameraStreamState == CameraStreamState.NotStreaming)
                {
                    if (_frameProcessingTimer != null)
                    {
                        _frameProcessingTimer.Cancel();
                        _frameProcessingSemaphore.Release();
                    }

                    // 15fpsで動作
                    // 66milsecond毎にCreatePeriodicTimerの第一引き数に渡したHandlerで指定したメソッドが実行される
                    TimeSpan timeInterval = TimeSpan.FromMilliseconds(66);
                    _frameProcessingTimer = ThreadPoolTimer.CreatePeriodicTimer(new TimerElapsedHandler(ProcessCurrentVideoFrame), timeInterval);
                    _videoProperties = _captureManager.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

                    await _captureManager.StartPreviewAsync();

                    WebCamCaptureElement.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Debug.Write("6");
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"error: {ex.Message}");
            }
        }

        // webcamを止める処理
        private async Task StopWebCameraAsync()
        {
            try
            {
                if (_frameProcessingTimer != null)
                {
                    // 15fps毎で設定されているTimerを停止する
                    _frameProcessingTimer.Cancel();
                }

                if (_captureManager != null && _captureManager.CameraStreamState != CameraStreamState.Shutdown)
                {
                    await _captureManager.StopPreviewAsync();
                    WebCamCaptureElement.Source = null;
                    _captureManager.Dispose();
                    _captureManager = null;

                    WebCamCaptureElement.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Debug.Write("5");
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"error: {ex.Message}");
            }
        }

        // Timerに設定した時間毎にWebCamのStreamから入力画像を受け取りそれを1フレームとしてTinyYOLOで評価する
        private async void ProcessCurrentVideoFrame(ThreadPoolTimer timer)
        {
            // 後半のWait(0)の意味がよく分からん
            if (_captureManager.CameraStreamState != CameraStreamState.Streaming || ! _frameProcessingSemaphore.Wait(0))
            {
                return;
            }

            try
            {
                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Bgra8;
                VideoFrame previewFrame = new VideoFrame(InputPixelFormat, (int)_videoProperties.Width, (int)_videoProperties.Height);
                // プレビューの開始
                //await _captureManager.StartPreviewAsync();
                // 入力フレームの取得
                await _captureManager.GetPreviewFrameAsync(previewFrame);
                // 以下のコードではダメ？
                // VideoFrame currentFrame = await _captureManager.GetPreviewFrameAsync(previewFrame);
                //VideoFrame currentFrame = await _captureManager.GetPreviewFrameAsync(previewFrame);
                await EvaluateVideoFrameAsync(previewFrame);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    await DrawOverlays(previewFrame);
                    previewFrame.Dispose();
                });
            }
            catch (Exception ex)
            {
                Debug.Write("4");
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"error: {ex.Message}");
            }
            finally
            {
                _frameProcessingSemaphore.Release();
            }
        }

        private async Task DrawYoloBoundingBoxAsync(SoftwareBitmap inputImage, YoloBoundingBox box)
        {
            // Scale is set to stretched 416*416 - Clip bounding boxes to image area
            var x = (uint)Math.Max(box.X, 0);
            var y = (uint)Math.Max(box.Y, 0);
            var w = (uint)Math.Min(this.OverlayCanvas.Width - x, box.Width);
            var h = (uint)Math.Min(this.OverlayCanvas.Height - y, box.Height);

            // 画像描画用のクラス
            var brush = new ImageBrush();
            var bitmapSource = new SoftwareBitmapSource();
            // 入力画像をbitmap sourceに指定
            await bitmapSource.SetBitmapAsync(inputImage);

            brush.ImageSource = bitmapSource;
            brush.Stretch = Stretch.Fill;

            this.OverlayCanvas.Background = brush;

            // BBの生成
            var r = new Rectangle();
            r.Tag = box;
            r.Width = w;
            r.Height = h;
            r.Fill = this.fillBrush;
            r.Stroke = this.lineBrush;
            r.StrokeThickness = this.lineThickness;
            r.Margin = new Thickness(x, y, 0, 0);

            // BBにつけるクラスラベル
            var tb = new TextBlock();
            tb.Margin = new Thickness(x + 4, y + 4, 0, 0);
            tb.Text = $"{box.Label} ({Math.Round(box.Confidence, 4).ToString()})";
            tb.FontWeight = FontWeights.Bold;
            tb.Width = 126;
            tb.Height = 21;
            tb.HorizontalTextAlignment = TextAlignment.Center;

            var textBack = new Rectangle();
            textBack.Width = 134;
            textBack.Height = 29;
            textBack.Fill = this.lineBrush;
            textBack.Margin = new Thickness(x, y, 0, 0);

            this.OverlayCanvas.Children.Add(textBack);
            this.OverlayCanvas.Children.Add(tb);
            this.OverlayCanvas.Children.Add(r);

        }

        private async Task EvaluateVideoFrameAsync(VideoFrame inputFrame)
        {
            if (inputFrame != null)
            {
                try
                {
                    // Create Bindings fpr the input and output buffer
                    var bindings = new LearningModelBindingPreview(this.model as LearningModelPreview);

                    // 出力のテンソルを受け取るための1Dの配列をあらかじめ用意しておく
                    var outputASrray = new List<float>();
                    //  TinyYOLOの出力のTensor
                    // 13 * 13 * 125
                    outputASrray.AddRange(new float[21125]);

                    bindings.Bind(this.inputImageDescription.Name, inputFrame);
                    bindings.Bind(this.outputTensorDescription.Name, outputASrray);

                    // Process the frame with the model
                    var stopwatch = Stopwatch.StartNew();
                    // この部分はTinyYOLOクラスのEvaluateAsyncではなくLearningModelPreviewクラスのEvaluateAsyncを使っている
                    var results = await this.model.EvaluateAsync(bindings, "TinyYOLO");
                    stopwatch.Stop();
                    var resultProbabilities = results.Outputs[this.outputTensorDescription.Name] as List<float>;

                    // Use out helper to parse to the YOLO outputs into bounding boxes with labels
                    this.boxes = this.parser.ParseOutputs(resultProbabilities.ToArray(), .3F);

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        Duration.Text = $"{1000f / stopwatch.ElapsedMilliseconds,4:f1} fps";
                        StatusBlock.Text = "Model Evaluation Completed";
                    });
                }
                catch (Exception ex)
                {
                    Debug.Write("3");
                    Debug.Write(ex.Message);
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"error: {ex.Message}");
                }
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ButtonRun.IsEnabled = true);
            }
        }

        private async Task LoadModelAsync(bool isGpu = true)
        {
            if (this.model != null)
            {
                return;
            }
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"Loading { MODEL_FILENAME } ... patience");

            try
            {
                // Load Model
                Debug.Write("1");
                var modelFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///Assets/{ MODEL_FILENAME }"));
                this.model = await LearningModelPreview.LoadModelFromStorageFileAsync(modelFile);
                this.model.InferencingOptions.ReclaimMemoryAfterEvaluation = true;
                this.model.InferencingOptions.PreferredDeviceKind = isGpu == true ? LearningModelDeviceKindPreview.LearningDeviceGpu : LearningModelDeviceKindPreview.LearningDeviceCpu;
                Debug.Write("2");

                //入力、出力を受ける変数。入力には画像、出力にテンソルが入る前提
                var inputFeatures = this.model.Description.InputFeatures.ToList();
                var outputFeatures = this.model.Description.OutputFeatures.ToList();

                this.inputImageDescription =
                    inputFeatures.FirstOrDefault(feature => feature.ModelFeatureKind == LearningModelFeatureKindPreview.Image) // 配列の先頭の要素を返すか、空なら規定値を返す
                    as ImageVariableDescriptorPreview;

                this.outputTensorDescription =
                    outputFeatures.FirstOrDefault(feature => feature.ModelFeatureKind == LearningModelFeatureKindPreview.Tensor)
                    as TensorVariableDescriptorPreview;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"Loaded { MODEL_FILENAME }. Press the camera button to start the webcam...");

            }
            catch (Exception ex)
            {
                Debug.Write("kuso");
                Debug.Write(ex.Message);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusBlock.Text = $"error: {ex.Message}");
                model = null;
            }
        }
    }
}
