﻿using System;
using FFImageLoading.Helpers;
using UIKit;
using System.Threading.Tasks;
using Foundation;
using System.Linq;
using System.IO;
using FFImageLoading.Extensions;
using System.Threading;
using FFImageLoading.Config;
using FFImageLoading.Cache;

namespace FFImageLoading.Work
{
    public class PlatformImageLoaderTask<TImageView> : ImageLoaderTask<UIImage, TImageView> where TImageView : class
    {
        static readonly SemaphoreSlim _decodingLock = new SemaphoreSlim(1, 1);

        public PlatformImageLoaderTask(ITarget<UIImage, TImageView> target, TaskParameter parameters, IImageService imageService, Configuration configuration, IMainThreadDispatcher mainThreadDispatcher)
            : base(ImageCache.Instance, configuration.DataResolverFactory ?? new DataResolvers.DataResolverFactory(), target, parameters, imageService, configuration, mainThreadDispatcher, true)
        {
            // do not remove! Kicks scale retrieval so it's available for all, without deadlocks due to accessing MainThread
            #pragma warning disable 0219
            var ignore = ScaleHelper.Scale;
            #pragma warning restore 0219
        }

        protected override Task SetTargetAsync(UIImage image, bool animated)
        {
            return MainThreadDispatcher.PostAsync(() =>
            {
                CancellationToken.ThrowIfCancellationRequested();
                TargetNative.Set(this, image, animated);
            });
        }

        protected async override Task<UIImage> GenerateImageAsync(string path, Stream imageData, ImageInformation imageInformation, bool enableTransformations)
        {
            UIImage imageIn = null;

            if (imageData == null)
                throw new ArgumentNullException(nameof(imageData));

            CancellationToken.ThrowIfCancellationRequested();

            using (imageData)
            {
                if (imageData.CanSeek)
                    imageData.Position = 0;

                // Special case to handle WebP decoding on iOS
                if (path.ToLowerInvariant().EndsWith(".webp", StringComparison.InvariantCulture))
                {
                    imageIn = new WebP.Touch.WebPCodec().Decode(imageData);
                }
                else
                {
                    var nsdata = NSData.FromStream(imageData);
                    int downsampleWidth = Parameters.DownSampleSize?.Item1 ?? 0;
                    int downsampleHeight = Parameters.DownSampleSize?.Item2 ?? 0;

                    if (Parameters.DownSampleUseDipUnits)
                    {
                        downsampleWidth = downsampleWidth.PointsToPixels();
                        downsampleHeight = downsampleHeight.PointsToPixels();
                    }

                    imageIn = nsdata.ToImage(new CoreGraphics.CGSize(downsampleWidth, downsampleHeight), ScaleHelper.Scale, NSDataExtensions.RCTResizeMode.ScaleAspectFill, imageInformation);
                }
            }

            CancellationToken.ThrowIfCancellationRequested();

            if (enableTransformations && Parameters.Transformations != null && Parameters.Transformations.Count > 0)
            {
                var transformations = Parameters.Transformations.ToList();

                await _decodingLock.WaitAsync().ConfigureAwait(false); // Applying transformations is both CPU and memory intensive

                try
                {
                    CancellationToken.ThrowIfCancellationRequested();

                    foreach (var transformation in transformations)
                    {
                        try
                        {
                            var old = imageIn;
                            var bitmapHolder = transformation.Transform(new BitmapHolder(imageIn));
                            imageIn = bitmapHolder.ToNative();

                            // Transformation succeeded, so garbage the source
                            if (old != null && old != imageIn && old.Handle != imageIn.Handle)
                                old.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(string.Format("Transformation error: {0}", transformation.Key), ex);
                            throw;
                        }
                    }
                }
                finally
                {
                    _decodingLock.Release();
                }
            }

            return imageIn;
        }
    }
}
