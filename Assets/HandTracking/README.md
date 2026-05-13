# Hand-Tracking Module — Руководство по интеграции

> **Цель:** Unity 2022/2023 LTS · WebGL · Screen Space – Overlay Canvas · 1920×1080

---

## Содержание

1. [Обзор](#1-обзор)
2. [Структура файлов](#2-структура-файлов)
3. [Требования](#3-требования)
4. [Пошаговая настройка](#4-пошаговая-настройка)
5. [Полная иерархия GameObject](#5-полная-иерархия-gameobject)
6. [Описание компонентов](#6-описание-компонентов)
7. [Справочник параметров Inspector](#7-справочник-параметров-inspector)
8. [Публичный API](#8-публичный-api)
9. [Fallback в редакторе](#9-fallback-в-редакторе)
10. [Интеграция с родительской игрой](#10-интеграция-с-родительской-игрой)
11. [Устранение неполадок](#11-устранение-неполадок)
12. [Архитектурная схема](#12-архитектурная-схема)

---

## 1. Обзор

Модуль предоставляет полноценную самодостаточную мини-игру с трекингом рук для Unity WebGL. Игрок встаёт перед веб-камерой; руки отслеживаются в реальном времени через MediaPipe Hands, работающий полностью в браузере (без сервера). Снаряды летят из центра экрана к рукам игрока; игрок должен физически двигать руками, чтобы их поймать.

| Функция | Реализация |
|---|---|
| UI разрешения камеры и WebCamTexture | `CameraPermissionManager.cs` |
| MediaPipe Hands (WASM) через JS | `mediapipe_hands.jslib` |
| Мост JS ↔ Unity | `HandTrackingBridge.cs` |
| Спрайты рук от первого лица | `HandVisualizer.cs` |
| Превью веб-камеры | `WebcamPreview.cs` |
| Спавн снарядов + дуга Безье | `ProjectileLauncher.cs` |
| Сужающийся маркер-прицел | `ReticleMarker.cs` |
| Определение поимки по перекрытию | `CatchZoneController.cs` |
| Опциональные частицы | `HandCatchParticles.cs` |
| Координатор-автомат состояний | `HandTrackingGameManager.cs` |

**Сторонние пакеты не требуются.** Модуль использует только встроенные средства Unity и нативный MediaPipe из `StreamingAssets`.

---

## 2. Структура файлов

```
Assets/HandTracking/
├── Plugins/
│   └── mediapipe_hands.jslib
├── Scripts/
│   ├── Core/
│   │   ├── CameraPermissionManager.cs
│   │   ├── HandTrackingBridge.cs
│   │   └── HandTrackingGameManager.cs
│   ├── Visualization/
│   │   ├── HandVisualizer.cs
│   │   ├── WebcamPreview.cs
│   │   └── HandCatchParticles.cs
│   └── Gameplay/
│       ├── ProjectileLauncher.cs
│       ├── CatchZoneController.cs
│       └── ReticleMarker.cs
├── Prefabs/
├── Sprites/
└── README_HandTracking.md

Assets/StreamingAssets/mediapipe/
├── hands.js
├── hands_solution_packed_assets_loader.js
├── hands_solution_packed_assets.data
├── hands_solution_wasm_bin.js
├── hands_solution_wasm_bin.wasm
├── hands_solution_simd_wasm_bin.js
├── hands_solution_simd_wasm_bin.wasm
├── hand_landmark_lite.tflite
├── hand_landmark_full.tflite
└── hands.binarypb
```

---

## 3. Требования

- Unity **2022.3 LTS** или **2023.x LTS**
- Платформа сборки: **WebGL** (`File → Build Settings → WebGL → Switch Platform`)
- Раздача через **HTTPS** (требуется `getUserMedia`). Chrome допускает `http://localhost` без HTTPS.
- Протестировано в браузерах: **Chrome 112+**, **Edge 112+**
- Пакет **TextMeshPro** (`Window → Package Manager → TextMeshPro`)
- При использовании **нового Input System**: `Edit → Project Settings → Player → Active Input Handling` → `Input System Package` или `Both`.

---

## 4. Пошаговая настройка

### 4.1 Создать корневой GameObject

1. Создать пустой GameObject в сцене
2. **Переименовать строго в `HandTrackingBridge`** — JS-плагин обращается к нему по имени через `SendMessage`
3. Добавить **все** следующие компоненты:

| Компонент | Обязателен |
|---|---|
| `HandTrackingBridge` | ✅ |
| `CameraPermissionManager` | ✅ |
| `HandTrackingGameManager` | ✅ |
| `ProjectileLauncher` | ✅ |
| `CatchZoneController` | ✅ |
| `HandVisualizer` | ✅ |
| `WebcamPreview` | ✅ |
| `HandCatchParticles` | опционально |

> **Почему всё на одном GameObject?**  
> JS `SendMessage("HandTrackingBridge", ...)` обращается к одному GameObject по имени. Все компоненты на нём получают вызов. Это исключает необходимость пересылки между объектами.

### 4.2 Настроить Canvas

1. Создать **Canvas** (`UI → Canvas`) с именем `HandTrackingCanvas`
2. **Render Mode:** `Screen Space – Overlay`
3. **Canvas Scaler:**
   - UI Scale Mode: `Scale With Screen Size`
   - Reference Resolution: `1920 × 1080`
   - Match: `0.5`
4. Добавить **Graphic Raycaster** (настройки по умолчанию)
5. Назначить этот Canvas в `HandTrackingGameManager → targetCanvas` и `ProjectileLauncher → targetCanvas`

### 4.3 Панель разрешения камеры

Создать следующую иерархию **внутри `HandTrackingCanvas`**:

```
PermissionPanel  (Image — полупрозрачный тёмный оверлей, anchors: stretch full screen)
├── RequestingContainer
│   ├── IconImage        (Image — иконка камеры)
│   ├── TitleText        (TextMeshProUGUI)
│   ├── BodyText         (TextMeshProUGUI)
│   └── AllowButton      (Button + TextMeshProUGUI)
└── DeniedContainer      (изначально НЕАКТИВЕН)
    ├── ErrorIcon        (Image)
    ├── DeniedReasonText (TextMeshProUGUI — заполняется в рантайме)
    └── RetryButton      (Button + TextMeshProUGUI)
```

**Привязка в Inspector `CameraPermissionManager`:**

| Поле | Назначить |
|---|---|
| `permissionPanel` | GameObject `PermissionPanel` |
| `bridge` | компонент `HandTrackingBridge` на корневом объекте |
| `requestingContainer` | `RequestingContainer` |
| `allowButton` | кнопка `AllowButton` |
| `spinnerObject` | *(опционально)* спиннер |
| `deniedContainer` | `DeniedContainer` |
| `deniedReasonText` | `DeniedReasonText` TMP_Text |
| `retryButton` | кнопка `RetryButton` |
| `autoRequestOnStart` | ✅ true |

**События Inspector `CameraPermissionManager`:**

| Событие | Когда срабатывает | Типичное использование |
|---|---|---|
| `OnGrantedEvent` | Браузер разрешил доступ к камере | Запустить мини-игру |
| `OnDeniedEvent` | Браузер запретил доступ к камере | Показать сообщение об ошибке |

### 4.4 Спрайты рук

Создать два Image GameObject **внутри `HandTrackingCanvas`**:

```
HandTrackingCanvas
├── HandLeft   (RectTransform + Image, pivot 0.5/0.5, size 120×180, Raycast Target = false)
└── HandRight  (RectTransform + Image, pivot 0.5/0.5, size 120×180, Raycast Target = false)
```

**Привязка в Inspector `HandVisualizer`:**

| Поле | Назначить |
|---|---|
| `bridge` | компонент `HandTrackingBridge` |
| `leftHandRect` | RectTransform `HandLeft` |
| `rightHandRect` | RectTransform `HandRight` |
| `leftHandImage` | Image `HandLeft` |
| `rightHandImage` | Image `HandRight` |
| `leftHandSprite` | спрайт левой руки |
| `rightHandSprite` | спрайт правой руки |
| `parentCanvas` | `HandTrackingCanvas` |
| `handScale` | `1.0` |
| `hiddenAlpha` | `0` |

### 4.5 Оверлей вспышки промаха

```
HandTrackingCanvas
└── MissFlashOverlay  (Image, anchors: stretch full screen, цвет белый, alpha 0, Raycast Target = false)
```

Назначить в `CatchZoneController → missFlashImage`.

### 4.6 Превью веб-камеры

1. Создать `RawImage` внутри `HandTrackingCanvas` — расположить и задать размер по желанию (например, правый нижний угол, 240×180 px)
2. Назначить `RawImage` в `WebcamPreview → previewImage`

Превью запускается автоматически при старте трекинга (`autoStartWithTracking = true`). Изображение зеркалируется горизонтально по умолчанию (`mirrorHorizontal = true`).

### 4.7 Префаб снаряда

1. Создать `Assets/HandTracking/Prefabs/Projectile.prefab`
2. Компоненты: `RectTransform` (size 80×80, pivot 0.5/0.5) + `Image` (Raycast Target = false)
3. Назначить в `ProjectileLauncher → projectilePrefab`

### 4.8 Префаб прицела

1. Создать `Assets/HandTracking/Prefabs/ReticleMarker.prefab`
2. Компоненты: `RectTransform` (size 120×120, pivot 0.5/0.5) + `Image` (кольцо, Raycast Target = false)
3. Добавить компонент `ReticleMarker`; назначить Image в `ReticleMarker → ringImage`
4. Назначить в `ProjectileLauncher → reticlePrefab`

### 4.9 Системы частиц (опционально)

1. Создать `CatchParticles.prefab` (вспышка искр, `Stop Action: Disable`)
2. Создать `MissParticles.prefab` (облачко при промахе)
3. Назначить в `HandCatchParticles → catchParticlePrefab` / `missParticlePrefab`

### 4.10 Привязка ссылок Inspector

**`HandTrackingGameManager`:**

| Поле | Назначить |
|---|---|
| `permissionManager` | `CameraPermissionManager` на корневом объекте |
| `bridge` | `HandTrackingBridge` на корневом объекте |
| `handVisualizer` | `HandVisualizer` на корневом объекте |
| `launcher` | `ProjectileLauncher` на корневом объекте |
| `catchZone` | `CatchZoneController` на корневом объекте |
| `particles` | `HandCatchParticles` *(опционально)* |
| `rulesPanel` | панель с правилами игры |
| `readyButton` | кнопка старта обратного отсчёта |
| `countdownPanel` | панель обратного отсчёта |
| `countdownText` | TMP_Text для цифр отсчёта |
| `progressBar` | компонент `HandTrackingProgressBar` *(опционально)* |
| `catchesToWin` | например `7` |
| `maxMisses` | например `0` (неограниченно) |
| `autoStart` | ✅ true |
| `countdownSeconds` | `3` |

**`CatchZoneController`:**

| Поле | Назначить |
|---|---|
| `bridge` | `HandTrackingBridge` |
| `launcher` | `ProjectileLauncher` |
| `targetCanvas` | `HandTrackingCanvas` |
| `catchRadius` | `90` |
| `missFlashImage` | Image `MissFlashOverlay` |

**`ProjectileLauncher`:**

| Поле | Назначить |
|---|---|
| `targetCanvas` | `HandTrackingCanvas` |
| `projectilePrefab` | `Projectile.prefab` |
| `reticlePrefab` | `ReticleMarker.prefab` |
| `projectileContainer` | RectTransform `HandTrackingCanvas` |

### 4.11 Настройки сборки WebGL

1. `Edit → Project Settings → Player → WebGL`
2. Publishing Settings → Compression Format: `Disabled` (или `Brotli` для продакшена)
3. Resolution: `1920 × 1080`, Run In Background: ✅
4. Other Settings → Allow downloads over HTTP: ✅
5. Включить **Development Build** при тестировании

### 4.12 Полоска прогресса (опционально)

1. Создать **Slider** внутри `HandTrackingCanvas`.
2. Отключить `Interactable`, установить `Transition` → `None`.
3. Добавить компонент `HandTrackingProgressBar`.
4. Назначить изображение `Fill` в `fillImage`.
5. (Опционально) Создать индикатор победы и назначить в `acceptedIndicator`.
6. Назначить в `HandTrackingGameManager → progressBar`.

---

## 5. Полная иерархия GameObject

```
Scene
│
├── HandTrackingBridge              ← СТРОГО такое имя
│   Компоненты:
│   ├── HandTrackingBridge
│   ├── CameraPermissionManager
│   ├── HandTrackingGameManager
│   ├── ProjectileLauncher
│   ├── CatchZoneController
│   ├── HandVisualizer
│   ├── WebcamPreview
│   └── HandCatchParticles          (опционально)
│
└── HandTrackingCanvas              (Canvas, Screen Space Overlay, 1920×1080)
    ├── PermissionPanel             (Image, full-screen overlay)
    │   ├── RequestingContainer
    │   │   ├── IconImage
    │   │   ├── TitleText           (TMP)
    │   │   ├── BodyText            (TMP)
    │   │   └── AllowButton
    │   └── DeniedContainer         (изначально неактивен)
    │       ├── ErrorIcon
    │       ├── DeniedReasonText    (TMP)
    │       └── RetryButton
    ├── HandLeft                    (Image, Raycast Target = false)
    ├── HandRight                   (Image, Raycast Target = false)
    ├── MissFlashOverlay            (Image, full-screen, Raycast Target = false, alpha = 0)
    ├── RulesPanel
    │   ├── RulesText               (TMP)
    │   └── ReadyButton             (Button)
    ├── CountdownPanel
    │   └── CountdownText           (TMP)
    ├── ProgressBar                 (Slider + HandTrackingProgressBar)
    └── WebcamPreviewPanel          (опциональная обёртка)
        └── WebcamRawImage          (RawImage → WebcamPreview.previewImage)
```

---

## 6. Описание компонентов

### 6.1 mediapipe_hands.jslib

Unity автоматически включает `.jslib` файлы в WebGL-сборку. Плагин:

1. Загружает MediaPipe Hands WASM из `StreamingAssets/mediapipe/` (офлайн-режим)
2. Открывает веб-камеру через `getUserMedia({ video: true })`
3. Каждый кадр анимации отправляет кадр видео в MediaPipe
4. По каждому результату сериализует запястье + 5 кончиков пальцев обеих рук в JSON
5. Вызывает `SendMessage("HandTrackingBridge", "OnHandData", json)`

**JSON-payload (отправляется каждый кадр):**
```json
{
  "hands": [
    {
      "label": "Left",
      "score": 0.97,
      "wrist":     { "x": 0.45, "y": 0.62 },
      "thumbTip":  { "x": 0.50, "y": 0.55 },
      "indexTip":  { "x": 0.42, "y": 0.40 },
      "middleTip": { "x": 0.41, "y": 0.38 },
      "ringTip":   { "x": 0.43, "y": 0.39 },
      "pinkyTip":  { "x": 0.46, "y": 0.42 }
    }
  ]
}
```
Все координаты нормализованы `[0, 1]`, начало координат — верхний левый угол. `selfieMode: true` — изображение зеркалируется.

### 6.2 HandTrackingBridge

Центральный хаб между JS-плагином и остальным C#-кодом.

- Объявляет `[DllImport("__Internal")]` стабы для всех jslib-функций
- Получает `OnHandData(string json)` через `SendMessage`, парсит, сглаживает позиции
- Конвертирует нормализованные координаты MediaPipe в пиксели экрана Unity (инвертирует ось Y)
- Предоставляет `LeftHandScreenPos`, `RightHandScreenPos`, `LeftHandVisible`, `RightHandVisible`
- Генерирует C#-события: `OnTrackingInitialisedEvent`, `OnTrackingStartedEvent`, `OnTrackingErrorEvent`
- **Умная сортировка рук:** при двух руках сортирует по X-координате, предотвращая путаницу левой/правой
- **Fallback в редакторе:** управляет правой рукой курсором мыши

### 6.3 CameraPermissionManager

Управляет UI-панелью разрешения браузера и запускает нативный запрос камеры Unity.

**Поток состояний:**
```
Start → ShowRequestingState → пользователь нажимает Allow → Application.RequestUserAuthorization
    ├── Granted (WebCamTexture.devices > 0) → HidePanel → fire OnGrantedEvent
    └── Denied  → ShowDeniedState → пользователь нажимает Retry → цикл
```

### 6.4 HandVisualizer

Перемещает два `Image` UI-элемента в соответствии с реальными позициями рук каждый кадр.

- Читает позиции из `HandTrackingBridge` каждый кадр
- Конвертирует пиксели экрана → `anchoredPosition` canvas через `RectTransformUtility`
- Плавно меняет alpha при появлении/исчезновении руки
- `SetHandSprite(HandSide, Sprite)` — смена спрайта в рантайме

### 6.5 WebcamPreview

Выводит живой поток веб-камеры в `RawImage`.

- Запускается автоматически при старте трекинга (`autoStartWithTracking = true`)
- Зеркалирует изображение горизонтально по умолчанию
- `StartPreview()` / `StopPreview()` — ручное управление

### 6.6 ProjectileLauncher

Управляет полным жизненным циклом снарядов: спавн, движение, выбор зоны приземления, тайминг прицела, пулинг.

#### Движение

Квадратичная кривая Безье каждый кадр:
```
P(t) = (1-t)² · P0  +  2(1-t)t · P1  +  t² · P2
```
- `P0` = центр canvas `(0, 0)`
- `P2` = целевая позиция приземления
- `P1` = средняя точка, смещённая вверх на `arcHeight`

Масштаб растёт от `startScale` → `endScale` по `scaleCurve`, создавая эффект перспективы «летит на игрока».

#### Зоны приземления

```
Canvas (1920×1080, центрированный pivot)
┌─────────────────────────────────────────┐  +540
│           [спавн: 0,0]                  │
│  ЛЕВАЯ ЗОНА         ПРАВАЯ ЗОНА         │
│  x: -800 до -150    x: 150 до 800       │  -150 (zoneYMax)
│  ┌──────────┐       ┌──────────┐        │
│  └──────────┘       └──────────┘        │  -400 (zoneYMin)
└─────────────────────────────────────────┘  -540
-960                                    +960
```

### 6.7 ReticleMarker

Сужающийся кольцевой маркер-предупреждение.

- Плавно появляется и масштабируется от 0 за `fadeInDuration`
- Масштаб от `ringStartScale` → `ringEndScale` за `warningDuration` секунд
- Цвет меняется от `startColor` (жёлтый) до `endColor` (красный)
- Опциональная пульсация (`enablePulse`) добавляет синусоидальное колебание
- `Dismiss()` — плавное исчезновение при успешной поимке

### 6.8 CatchZoneController

Определяет, когда рука перекрывает снаряд.

**Определение:** каждый кадр для каждого активного снаряда:
1. Проверить, близко ли снаряд к цели: `distance(projPos, targetPos) <= catchTargetProximity`
2. Проверить перекрытие рукой: `distance(handCanvasPos, projPos) < catchRadius`

**При поимке:** вызывает `launcher.NotifyCaught(proj)`, генерирует `OnCatch`.  
**При промахе:** генерирует `OnMiss`, запускает вспышку оверлея.

### 6.9 HandCatchParticles

Опциональные пулированные частицы. Полностью отвязан — удаление не влияет на геймплей.

### 6.10 HandTrackingGameManager

Координатор верхнего уровня. Автомат состояний:

```
Idle
 │  StartMinigame()
 ▼
WaitingForPermission  ←── OnDenied (остаётся здесь, UI показывает retry)
 │  OnGranted
 ▼
InitialisingTracking
 │  OnTrackingInitialised → bridge.StartTracking()
 │  OnTrackingStarted     → Показать панель правил
 ▼
ShowingRules
 │  Пользователь нажимает "Ready"
 ▼
Countdown (3.. 2.. 1..)
 │  Отсчёт завершён    → launcher.StartLaunching()
 ▼
Playing
 │  catches >= catchesToWin  →  OnWin  →  Finished
 │  misses  >= maxMisses     →  OnLose →  Finished
 │  StopMinigame()           →           Finished
```

---

## 7. Справочник параметров Inspector

### CameraPermissionManager

| Параметр | По умолчанию | Описание |
|---|---|---|
| `autoRequestOnStart` | `true` | Запросить разрешение при Start |
| `maxAutoRetries` | `0` | Тихие повторы перед показом UI отказа |

### HandTrackingBridge

| Параметр | По умолчанию | Описание |
|---|---|---|
| `maxHands` | `2` | Максимум рук для определения |
| `minDetectionConfidence` | `0.7` | Минимальная уверенность детекции |
| `minTrackingConfidence` | `0.5` | Минимальная уверенность трекинга |
| `smoothingFactor` | `0.2` | Скорость Lerp позиций рук (0=нет движения, 1=мгновенно) |
| `editorMouseFallback` | `true` | Управлять правой рукой мышью в редакторе |

### HandVisualizer

| Параметр | По умолчанию | Описание |
|---|---|---|
| `handScale` | `1.0` | Масштаб изображений рук |
| `visibleAlpha` | `1.0` | Alpha при обнаруженной руке |
| `hiddenAlpha` | `0.0` | Alpha при отсутствии руки |
| `alphaFadeSpeed` | `8.0` | Скорость затухания |

### ProjectileLauncher

| Параметр | По умолчанию | Описание |
|---|---|---|
| `spawnInterval` | `2.0s` | Время между спавнами |
| `maxActiveProjectiles` | `3` | Максимум одновременных снарядов |
| `travelDuration` | `2.0s` | Время полёта от спавна до цели |
| `warningLeadTime` | `1.5s` | За сколько секунд до прилёта появляется прицел |
| `arcHeight` | `200px` | Высота пика дуги Безье |
| `startScale` | `0.3` | Масштаб при спавне (маленький = далеко) |
| `endScale` | `1.4` | Масштаб при приземлении (большой = близко) |
| `leftZoneXMin/Max` | `-800`/`-150` | Диапазон X левой зоны поимки |
| `rightZoneXMin/Max` | `150`/`800` | Диапазон X правой зоны поимки |
| `zoneYMin/Max` | `-400`/`-150` | Вертикальный диапазон зоны приземления |

### ReticleMarker

| Параметр | По умолчанию | Описание |
|---|---|---|
| `warningDuration` | `1.5s` | Длительность анимации сужения |
| `ringStartScale` | `3.0` | Начальный масштаб кольца |
| `ringEndScale` | `0.8` | Конечный масштаб кольца |
| `fadeInDuration` | `0.3s` | Время появления |
| `startColor` | жёлтый | Цвет кольца в начале |
| `endColor` | красный | Цвет кольца в конце |
| `enablePulse` | `true` | Добавить пульсирующее колебание |

### CatchZoneController

| Параметр | По умолчанию | Описание |
|---|---|---|
| `catchRadius` | `90px` | Радиус определения поимки |
| `catchTargetProximity` | `150px` | Макс. расстояние от цели для засчитывания поимки |
| `requireHandVisible` | `true` | Засчитывать только при видимой руке |
| `missFlashDuration` | `0.25s` | Длительность вспышки экрана при промахе |

### HandTrackingGameManager

| Параметр | По умолчанию | Описание |
|---|---|---|
| `catchesToWin` | `10` | Поимок для победы (0 = бесконечно) |
| `maxMisses` | `3` | Промахов до поражения (0 = бесконечно) |
| `autoStart` | `true` | Вызвать StartMinigame() при Start |
| `countdownSeconds` | `3` | Длительность обратного отсчёта |

---

## 8. Публичный API

### HandTrackingGameManager

```csharp
gameManager.StartMinigame();
gameManager.PauseMinigame();
gameManager.ResumeMinigame();
gameManager.StopMinigame();

HandTrackingState state = gameManager.CurrentState;
int catches = gameManager.CatchCount;
int misses  = gameManager.MissCount;
```

### HandTrackingBridge

```csharp
Vector2 leftPos  = bridge.LeftHandScreenPos;
Vector2 rightPos = bridge.RightHandScreenPos;
bool leftVisible = bridge.IsHandVisible(HandSide.Left);
IReadOnlyList<HandLandmarkData> landmarks = bridge.LastLandmarks;
```

### WebcamPreview

```csharp
webcamPreview.StartPreview();
webcamPreview.StopPreview();
bool isVisible = webcamPreview.IsVisible;
```

### CameraPermissionManager

```csharp
// Подписка в коде
permissionManager.OnGranted += () => { /* ... */ };
permissionManager.OnDenied  += reason => { /* ... */ };

// Или подключить OnGrantedEvent / OnDeniedEvent в Inspector как любой UnityEvent
```

---

## 9. Fallback в редакторе

При запуске в **Unity Editor** `HandTrackingBridge` автоматически использует **курсор мыши** как позицию правой руки. Это позволяет тестировать полный игровой цикл без камеры и WebGL-сборки.

- Двигать мышь = симулировать правую руку
- Левая рука скрыта
- Вся логика поимки/промаха работает нормально
- Работает с Legacy Input Manager и новым Input System

Отключить: снять галочку `editorMouseFallback` на `HandTrackingBridge`.

---

## 10. Интеграция с родительской игрой

Подключить события `HandTrackingGameManager` в Inspector или в коде:

```csharp
// В контроллере экрана с пиратом:
void Start()
{
    handTrackingManager.OnCatch.AddListener(HandleCatch);
    handTrackingManager.OnMiss.AddListener(HandleMiss);
    handTrackingManager.OnWin.AddListener(HandleWin);
}

void HandleCatch()
{
    musiclingsCaught++;
    PlayCatchSound();
}

void HandleWin()
{
    // Все музыкальчики спасены — анимация испуганного пирата, перелистывание страницы
    StartCoroutine(PirateFleeSequence());
}
```

Запустить мини-игру при открытии страницы:

```csharp
public void OnPage3Opened()
{
    handTrackingManager.StartMinigame();
}
```

Подключить `CameraPermissionManager.OnGrantedEvent` в Inspector:
1. Выбрать GameObject `HandTrackingBridge`
2. Найти `CameraPermissionManager → OnGrantedEvent`
3. Нажать `+`, перетащить `HandTrackingGameManager`, выбрать `StartMinigame()`

---

## 11. Устранение неполадок

| Симптом | Вероятная причина | Решение |
|---|---|---|
| `getUserMedia not supported` | HTTP вместо HTTPS | Раздавать через HTTPS или использовать `localhost` |
| Панель разрешения не исчезает | Браузер заблокировал запрос | Убедиться, что пользователь нажал «Разрешить» в браузере |
| Руки не двигаются | Несовпадение имени GameObject | Переименовать корневой объект строго в `HandTrackingBridge` |
| MediaPipe не загружается | Отсутствуют файлы в StreamingAssets | Проверить наличие всех 10 файлов в `StreamingAssets/mediapipe/` |
| Снаряды не спавнятся | `targetCanvas` не назначен | Назначить `HandTrackingCanvas` в `ProjectileLauncher → targetCanvas` |
| Радиус поимки ощущается неправильно | Несовпадение масштаба canvas | Убедиться, что `CatchZoneController → targetCanvas` совпадает с `ProjectileLauncher → targetCanvas` |
| Превью камеры чёрное | Разрешение ещё не выдано | `WebcamPreview` стартует после `OnTrackingStartedEvent` — сначала нужно разрешение |
| Превью зеркалируется неправильно | Настройка `mirrorHorizontal` | Переключить `WebcamPreview → mirrorHorizontal` |
| Частицы не воспроизводятся | `enableParticles = false` или отсутствует префаб | Проверить Inspector `HandCatchParticles` |
| Чёрный экран в WebGL | Несовпадение сжатия | Настроить сервер на отдачу `.br`/`.gz` или отключить сжатие в настройках сборки |
| Высокая нагрузка CPU в браузере | Сложность модели MediaPipe | `modelComplexity: 0` уже установлен в jslib (lite-модель) |

---

## 12. Архитектурная схема

```
Браузер (JS)                          Unity (C#)
─────────────────────────────────     ──────────────────────────────────────────
Поток веб-камеры
    │
    ▼
MediaPipe Hands WASM
    │  onResults callback
    ▼
HT_ProcessResults()
    │  JSON через SendMessage("HandTrackingBridge", "OnHandData", json)
    ▼                                 HandTrackingBridge.OnHandData(json)
                                           │  парсинг + сглаживание
                                           ├──► HandVisualizer      (двигает спрайты)
                                           └──► CatchZoneController (проверка попадания)
                                                     │
                                      ProjectileLauncher ──► ReticleMarker
                                                     │
                                           OnCatch / OnMiss
                                                     │
                                      HandTrackingGameManager
                                           │         │
                                        OnWin     OnLose
                                           │
                                      Родительская игра (перелистывание страницы, анимации)

Поток разрешений:
CameraPermissionManager.RequestPermission()
     │
     ├──► Application.RequestUserAuthorization(WebCam)
     │
     └──► Ожидание WebCamTexture.devices.Length > 0
               │
               ├──► HidePanel()
               ├──► OnGranted (C# event)
               └──► OnGrantedEvent (UnityEvent — виден в Inspector)
```
