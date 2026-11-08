use crate::models::Track;
use windows::core::HSTRING;
use windows::Media::Playback::MediaPlayer;
use windows::Media::{MediaPlaybackType, SystemMediaTransportControls};

pub struct SmtcController {
    smtc: SystemMediaTransportControls,
}

impl SmtcController {
    pub fn new(player: &MediaPlayer) -> windows::core::Result<Self> {
        let smtc = player.SystemMediaTransportControls()?;
        smtc.SetIsEnabled(true)?;
        smtc.SetIsPlayEnabled(true)?;
        smtc.SetIsPauseEnabled(true)?;
        smtc.SetIsNextEnabled(true)?;
        smtc.SetIsPreviousEnabled(true)?;

        Ok(Self { smtc })
    }

    pub fn update_track(&self, track: &Track) -> windows::core::Result<()> {
        let updater = self.smtc.DisplayUpdater()?;
        updater.SetType(MediaPlaybackType::Music)?;
        updater.SetAppMediaId(&HSTRING::from("MusicPower3"))?;

        let music_props = updater.MusicProperties()?;
        music_props.SetTitle(&HSTRING::from(track.display_title()))?;
        music_props.SetArtist(&HSTRING::from(track.display_artist()))?;
        music_props.SetAlbumTitle(&HSTRING::from(track.display_album()))?;

        updater.Update()?;
        Ok(())
    }
}

