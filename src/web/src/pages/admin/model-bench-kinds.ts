/** The kinds of model the bench tests: chat, and each media model by what it does. */
export const benchKinds: Record<string, { what: string; capability: string; capacity: string }> = {
  image_generation: { what: 'a picture', capability: 'It makes a picture, as big as asked (checked from its header).', capacity: 'Pictures a minute (the GPU makes one at a time).' },
  video_generation: { what: 'a clip', capability: 'It makes a short clip (checked as a video file).', capacity: 'Clips a minute.' },
  audio_speech: { what: 'a sentence', capability: 'It speaks a sentence, and a speech-to-text model hears it back as said (most of its words).', capacity: 'Sentences a minute, one and more at once.' },
  audio_transcription: { what: 'a sentence', capability: 'It hears a sentence a speech model said (most of its words).', capacity: 'Sentences heard a minute, one and more at once.' },
}

export const testable = (mode: string) => mode === 'chat' || mode in benchKinds
