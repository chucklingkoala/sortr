//! The Windows accent colour, so the page can use it as WPF's Fluent theme did.

use serde::Serialize;
use windows::UI::ViewManagement::{UIColorType, UISettings};

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AccentColors {
    pub accent: String,
    pub accent_dark1: String,
    pub accent_dark2: String,
    pub accent_light1: String,
    pub accent_light2: String,
}

pub fn accent_colors() -> windows::core::Result<AccentColors> {
    let settings = UISettings::new()?;
    let hex = |kind: UIColorType| -> windows::core::Result<String> {
        let c = settings.GetColorValue(kind)?;
        Ok(format!("#{:02x}{:02x}{:02x}", c.R, c.G, c.B))
    };
    Ok(AccentColors {
        accent: hex(UIColorType::Accent)?,
        accent_dark1: hex(UIColorType::AccentDark1)?,
        accent_dark2: hex(UIColorType::AccentDark2)?,
        accent_light1: hex(UIColorType::AccentLight1)?,
        accent_light2: hex(UIColorType::AccentLight2)?,
    })
}

#[cfg(test)]
mod tests {
    #[test]
    fn reads_accent_colours() {
        let colors = crate::shell::on_sta_thread(super::accent_colors).expect("accent colours");
        assert!(colors.accent.starts_with('#') && colors.accent.len() == 7);
    }
}
