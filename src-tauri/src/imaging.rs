//! Decodes images with Windows' own codecs (WIC) for formats WebView2 can't show (TIFF, HEIC, JPEG XR, …), and
//! carries pixels to the page as raw RGBA.

use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, GUID_WICPixelFormat32bppRGBA, IWICBitmapFrameDecode, IWICBitmapSource, IWICImagingFactory,
    WICBitmapDitherTypeNone, WICBitmapInterpolationModeFant, WICBitmapPaletteTypeCustom, WICBitmapTransformRotate90,
    WICBitmapTransformRotate180, WICBitmapTransformRotate270, WICDecodeMetadataCacheOnDemand,
};
use windows::Win32::System::Com::StructuredStorage::{PROPVARIANT, PropVariantClear, PropVariantToUInt16};
use windows::Win32::System::Com::{CLSCTX_INPROC_SERVER, CoCreateInstance};
use windows::core::{Interface, w};

/// Straight (not premultiplied) RGBA pixels, plus the image's full size before any downscaling.
pub struct Pixels {
    pub width: u32,
    pub height: u32,
    pub full_width: u32,
    pub full_height: u32,
    pub rgba: Vec<u8>,
}

impl Pixels {
    /// From a GDI bitmap's premultiplied BGRA. Thumbnails of opaque images often come with an all-zero alpha channel;
    /// those are treated as opaque.
    pub fn from_premultiplied_bgra(width: u32, height: u32, mut data: Vec<u8>) -> Pixels {
        let has_alpha = data.chunks_exact(4).any(|px| px[3] != 0);
        for px in data.chunks_exact_mut(4) {
            let (b, g, r) = (px[0], px[1], px[2]);
            let a = if has_alpha { px[3] } else { 255 };
            let straight = |c: u8| if a == 0 || a == 255 { c } else { ((c as u32 * 255 + a as u32 / 2) / a as u32).min(255) as u8 };
            px.copy_from_slice(&[straight(r), straight(g), straight(b), a]);
        }
        Pixels { width, height, full_width: width, full_height: height, rgba: data }
    }

    /// The wire format the page reads: four little-endian u32s (width, height, full width, full height), then RGBA.
    pub fn into_bytes(self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(16 + self.rgba.len());
        for n in [self.width, self.height, self.full_width, self.full_height] {
            bytes.extend_from_slice(&n.to_le_bytes());
        }
        bytes.extend_from_slice(&self.rgba);
        bytes
    }
}

/// Decodes the first frame of an image, upright (EXIF orientation applied), with its longer side at most `max_side`
/// pixels. The file is read into memory first so it's never held open. Call on a COM-initialised thread.
pub fn decode(path: &str, max_side: u32) -> windows::core::Result<Pixels> {
    let bytes = std::fs::read(path).map_err(|e| windows::core::Error::new(windows::core::HRESULT(-1), e.to_string()))?;
    unsafe {
        let factory: IWICImagingFactory = CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)?;
        let stream = factory.CreateStream()?;
        stream.InitializeFromMemory(&bytes)?;
        let decoder = factory.CreateDecoderFromStream(&stream, std::ptr::null(), WICDecodeMetadataCacheOnDemand)?;
        let frame = decoder.GetFrame(0)?;
        let (mut width, mut height) = (0, 0);
        frame.GetSize(&mut width, &mut height)?;
        let orientation = read_orientation(&frame);
        let mut source: IWICBitmapSource = frame.cast()?;

        let longest = width.max(height);
        if longest > max_side {
            let scale = max_side as f64 / longest as f64;
            let scaled = |n: u32| ((n as f64 * scale).round() as u32).max(1);
            let scaler = factory.CreateBitmapScaler()?;
            scaler.Initialize(&source, scaled(width), scaled(height), WICBitmapInterpolationModeFant)?;
            source = scaler.cast()?;
        }

        // Phone photos are often stored sideways with an orientation tag.
        let transform = match orientation {
            3 => Some(WICBitmapTransformRotate180),
            6 => Some(WICBitmapTransformRotate90),
            8 => Some(WICBitmapTransformRotate270),
            _ => None,
        };
        if let Some(transform) = transform {
            let rotator = factory.CreateBitmapFlipRotator()?;
            rotator.Initialize(&source, transform)?;
            source = rotator.cast()?;
            if orientation != 3 {
                (width, height) = (height, width);
            }
        }

        let converter = factory.CreateFormatConverter()?;
        converter.Initialize(
            &source,
            &GUID_WICPixelFormat32bppRGBA,
            WICBitmapDitherTypeNone,
            None,
            0.0,
            WICBitmapPaletteTypeCustom,
        )?;
        let (mut out_width, mut out_height) = (0, 0);
        converter.GetSize(&mut out_width, &mut out_height)?;
        let mut rgba = vec![0u8; out_width as usize * out_height as usize * 4];
        converter.CopyPixels(std::ptr::null(), out_width * 4, &mut rgba)?;
        Ok(Pixels { width: out_width, height: out_height, full_width: width, full_height: height, rgba })
    }
}

fn read_orientation(frame: &IWICBitmapFrameDecode) -> u16 {
    unsafe {
        // Formats without metadata support (BMP, GIF, ICO, …) have no reader; that's fine.
        let Ok(reader) = frame.GetMetadataQueryReader() else { return 1 };
        let mut value = PROPVARIANT::default();
        if reader.GetMetadataByName(w!("System.Photo.Orientation"), &mut value).is_err() {
            return 1;
        }
        let orientation = PropVariantToUInt16(&value).unwrap_or(1);
        let _ = PropVariantClear(&mut value);
        orientation
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn opaque_thumbnail_with_zero_alpha_becomes_opaque() {
        let px = Pixels::from_premultiplied_bgra(1, 1, vec![10, 20, 30, 0]);
        assert_eq!(px.rgba, [30, 20, 10, 255]);
    }

    #[test]
    fn premultiplied_alpha_is_undone() {
        // 50% transparent pure red, premultiplied: red channel stored as 128.
        let px = Pixels::from_premultiplied_bgra(2, 1, vec![0, 0, 128, 128, 0, 0, 0, 0]);
        assert_eq!(px.rgba, [255, 0, 0, 128, 0, 0, 0, 0]);
    }

    #[test]
    fn wire_format_has_header_then_pixels() {
        let bytes = Pixels { width: 2, height: 1, full_width: 4, full_height: 2, rgba: vec![1; 8] }.into_bytes();
        assert_eq!(&bytes[..16], &[2, 0, 0, 0, 1, 0, 0, 0, 4, 0, 0, 0, 2, 0, 0, 0]);
        assert_eq!(bytes.len(), 24);
    }
}

#[cfg(test)]
mod decode_tests {
    use super::*;
    use crate::shell::on_sta_thread;

    const ICON: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/icons/icon.png");

    #[test]
    fn decodes_png_at_full_size() {
        let pixels = on_sta_thread(|| decode(ICON, 4096)).expect("decode");
        assert_eq!((pixels.width, pixels.height), (256, 256));
        assert_eq!((pixels.full_width, pixels.full_height), (256, 256));
        assert_eq!(pixels.rgba.len(), 256 * 256 * 4);
    }

    #[test]
    fn downscales_to_max_side_but_reports_full_size() {
        let pixels = on_sta_thread(|| decode(ICON, 100)).expect("decode");
        assert_eq!((pixels.width, pixels.height), (100, 100));
        assert_eq!((pixels.full_width, pixels.full_height), (256, 256));
    }

    #[test]
    fn non_image_fails() {
        let manifest = concat!(env!("CARGO_MANIFEST_DIR"), "/Cargo.toml");
        assert!(on_sta_thread(|| decode(manifest, 100)).is_err());
    }
}
