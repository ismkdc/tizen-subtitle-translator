# Third-party notices

This repository contains original code plus references to third-party tools,
libraries, models, and services. Their licences apply to their parts.

## Code and libraries

| Project | Licence | Use |
|---|---|---|
| [Vetro](https://github.com/andrearicci93/vetro) | MIT | Tizen root manager / root agent (not bundled) |
| [Chris Ritsen — samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root) | see repository | Root research (not bundled) |
| [Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung) | see repository | WGT signing/install (not bundled) |
| [MNN](https://github.com/alibaba/MNN) | Apache-2.0 | Neural OCR runtime (not bundled) |
| [Tesseract](https://github.com/tesseract-ocr/tesseract) | Apache-2.0 | OCR engine (not bundled) |
| [Leptonica](http://www.leptonica.org/) | BSD-2-Clause | Tesseract dependency (not bundled) |
| [Argos Translate](https://github.com/argosopentech/argos-translate) | MIT | Local translation server |
| [CTranslate2](https://github.com/OpenNMT/CTranslate2) | MIT | NLLB inference |
| [Transformers](https://github.com/huggingface/transformers) | Apache-2.0 | NLLB tokenizer |
| [SentencePiece](https://github.com/google/sentencepiece) | Apache-2.0 | Tokenization |

## Models

| Model | Licence | Notes |
|---|---|---|
| Tesseract `eng.traineddata` | Apache-2.0 | `tessdata` / `tessdata_best` |
| `rec.mnn` + `rec-keys.txt` | not specified here | Project-specific OCR model; not included |
| [OPUS-MT en→tr](https://huggingface.co/Helsinki-NLP/opus-mt-en-tr) | CC-BY-4.0 | Argos package |
| [NLLB-200-distilled-600M](https://huggingface.co/facebook/nllb-200-distilled-600M) | **CC-BY-NC-4.0** | Non-commercial |

## Services

| Service | Terms |
|---|---|
| DeepL API | DeepL terms of service; free tier quotas apply |
| Samsung `libdisplay-capture-api` / TEE capture | proprietary; not distributed here |
| SamsungOne font | proprietary; used by name only, with fallback fonts |

## Content

Game subtitles, films, and broadcasts are copyrighted by their owners. This
project is a personal accessibility/research tool; translations and captured
frames are not redistributed by this repository.

If you believe a file in this repository infringes your rights, open an issue.
