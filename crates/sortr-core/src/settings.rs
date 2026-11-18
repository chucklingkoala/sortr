use std::path::PathBuf;

use serde::{Deserialize, Serialize};

/// The folders from the last scan. Field names match Sortr 1.x's `settings.json`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppSettings {
    pub source_folder: Option<String>,
    pub targets_folder: Option<String>,
}

pub struct SettingsStore {
    path: PathBuf,
}

impl SettingsStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        SettingsStore { path: path.into() }
    }

    /// The saved settings, or defaults when there are none. A corrupt file shouldn't stop the app from starting.
    pub fn load(&self) -> AppSettings {
        std::fs::read_to_string(&self.path).ok().and_then(|text| serde_json::from_str(&text).ok()).unwrap_or_default()
    }

    pub fn save(&self, settings: &AppSettings) -> std::io::Result<()> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(settings).map_err(std::io::Error::other)?;
        std::fs::write(&self.path, json)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips() {
        let dir = tempfile::tempdir().unwrap();
        let store = SettingsStore::new(dir.path().join("sub").join("settings.json"));
        let settings =
            AppSettings { source_folder: Some(r"C:\Downloads".into()), targets_folder: Some(r"D:\Clients".into()) };
        store.save(&settings).unwrap();
        assert_eq!(store.load(), settings);
    }

    #[test]
    fn reads_sortr_1_settings() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        std::fs::write(&path, "{\r\n  \"SourceFolder\": \"C:\\\\Downloads\",\r\n  \"TargetsFolder\": null\r\n}").unwrap();
        let settings = SettingsStore::new(&path).load();
        assert_eq!(settings.source_folder.as_deref(), Some(r"C:\Downloads"));
        assert_eq!(settings.targets_folder, None);
    }

    #[test]
    fn missing_or_corrupt_file_gives_defaults() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        assert_eq!(SettingsStore::new(&path).load(), AppSettings::default());
        std::fs::write(&path, "{ not json").unwrap();
        assert_eq!(SettingsStore::new(&path).load(), AppSettings::default());
    }
}
