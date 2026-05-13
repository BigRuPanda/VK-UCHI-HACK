# ReadingModule — Модуль чтения для Unity WebGL

Подключаемый модуль для детской образовательной игры.  
Ребёнок читает предложения вслух; каждое слово проверяется через офлайн-распознавание речи Vosk (WASM).  
Правильно прочитанные слова подсвечиваются **зелёным** с анимацией, неправильные трясутся красным — ребёнок должен перечитать их.  
Завершение предложения разблокирует настроенные фоновые объекты сцены.

---

## Архитектура

### Двухслойная структура префаба WordToken

`HorizontalLayoutGroup` управляет позицией **прямых дочерних объектов** каждый кадр.  
Если DOTween двигает тот же `RectTransform` — Layout Group перезаписывает позицию и все слова слипаются в одну точку.

**Решение:** двухслойная структура префаба:

```
WordToken (root)          ← Layout Group управляет ЭТИМ объектом
└── WordVisual (child)    ← DOTween анимирует ЭТОТ объект
    └── TextMeshProUGUI
```

Корневой объект — только «слот» для Layout Group.  
Все анимации (float, shake, punch) работают на дочернем `WordVisual`.

### Слоговое чтение

Система поддерживает слоговое чтение: если ребёнок произносит «мо» и «ст» по отдельности — слово «мост» засчитывается. Буфер слогов накапливается и сравнивается с ожидаемым словом.

### Офлайн STT (Vosk)

Распознавание речи работает полностью локально через Vosk WASM — никакие данные не отправляются на сервер. Модель `vosk-model-small-ru` загружается из `StreamingAssets`.

---

## Требования

| Зависимость | Версия | Примечание |
|---|---|---|
| Unity | 2022 LTS или 2023 LTS | Нужен WebGL Build Support |
| TextMeshPro | встроен | `Window → Package Manager` если отсутствует |
| DoTween | Free / Pro | [Asset Store](https://assetstore.unity.com/packages/tools/animation/dotween-hotween-v2-27676) |
| Input System | встроен | `Window → Package Manager → Input System → Install` |

> Игра должна раздаваться через **HTTPS** или `localhost` — иначе браузер не даст доступ к микрофону.

---

## Структура файлов

```
Assets/ReadingModule/
├── Data/
│   └── SentenceData.cs
├── Plugins/
│   ├── MicrophonePlugin.jslib
│   ├── Microphone.cs
│   └── WebGL/
│       └── VoskBridge.jslib
├── Scripts/
│   ├── SpeechBridge.cs
│   ├── WordToken.cs                ← анимации каждого слова
│   ├── MicrophonePermissionHandler.cs
│   ├── MicLevelIndicator.cs
│   └── ReadingController.cs       ← спавнит WordToken префабы
└── README.md

Assets/StreamingAssets/
├── vosk/
│   ├── vosk-browser.js
│   └── vosk-worker.js
└── vosk-model-small-ru/            ← офлайн модель распознавания
```

---

## Полная инструкция по настройке

### Шаг 1 — Импорт зависимостей

**DoTween:**
1. `Window → Package Manager → + → Add package from git URL`  
   или скачать с Asset Store.
2. После импорта: `Tools → DOTween Utility Panel → Setup DOTween`.

**Input System:**
1. `Window → Package Manager → Input System → Install`.
2. `Edit → Project Settings → Player → Other Settings → Active Input Handling` → **Input System Package** (или **Both**).
3. Unity попросит перезапуститься — согласиться.

---

### Шаг 2 — Создание префаба WordToken

Это самый важный шаг. Структура должна быть **строго двухслойной**.

#### 2.1 Создать корневой объект

1. В **Hierarchy** щёлкнуть правой кнопкой → **UI → Empty**.
2. Переименовать объект в **`WordToken`**.
3. Выбрать `WordToken` в Hierarchy. В **Inspector** найти компонент **Rect Transform**:

   | Поле | Значение |
   |---|---|
   | **Anchors** | Min X=0, Min Y=0, Max X=0, Max Y=0 |
   | **Pivot** | X=0.5, Y=0.5 |
   | **Width** | 0 *(Layout Group задаст автоматически)* |
   | **Height** | 80 |

#### 2.2 Добавить компонент WordToken

1. Выбрать `WordToken` → **Add Component → Scripts → ReadingModule → Word Token**.

#### 2.3 Создать дочерний объект WordVisual

1. Щёлкнуть правой кнопкой на `WordToken` в Hierarchy → **UI → Empty**.
2. Переименовать в **`WordVisual`**.
3. Выбрать `WordVisual`. В **Rect Transform**:

   | Поле | Значение |
   |---|---|
   | **Anchors** | Min X=0, Min Y=0, Max X=1, Max Y=1 *(Stretch по обеим осям)* |
   | **Left / Right / Top / Bottom** | 0, 0, 0, 0 |
   | **Pivot** | X=0.5, Y=0.5 |

#### 2.4 Добавить TextMeshProUGUI на WordVisual

1. Выбрать `WordVisual` → **Add Component → UI → Text - TextMeshPro**.
2. Настроить компонент **TextMeshPro - Text (UI)**:

   | Поле | Значение |
   |---|---|
   | **Font Size** | 48–64 |
   | **Vertex Color** | `(0.72, 0.72, 0.72, 1)` — серый (idle) |
   | **Alignment** | Center / Middle |
   | **Wrapping** | **Disabled** |
   | **Overflow** | **Overflow** |
   | **Rich Text** | ✓ Enabled |
   | **Extra Settings → Margins** | 4, 0, 4, 0 |

#### 2.5 (Опционально) Добавить ParticleSystem для искр

1. Щёлкнуть правой кнопкой на `WordVisual` → **Effects → Particle System**.
2. Переименовать в **`CorrectParticles`**.
3. Настроить **Particle System**:

   | Секция | Поле | Значение |
   |---|---|---|
   | **Main** | Duration | 0.5 |
   | **Main** | Looping | ✗ Off |
   | **Main** | Start Lifetime | 0.5 |
   | **Main** | Start Speed | 80 |
   | **Main** | Start Size | 8 |
   | **Main** | Start Color | Gold `#FFD700` |
   | **Main** | Simulation Space | **World** |
   | **Main** | Play On Awake | ✗ **Off** |
   | **Emission** | Rate over Time | 0 |
   | **Emission** | Bursts → + | Time=0, Count=5, Cycles=1 |
   | **Shape** | Shape | Sphere |
   | **Shape** | Radius | 0.05 |

#### 2.6 Назначить ссылки в WordToken Inspector

| Поле | Что перетащить |
|---|---|
| **Visual Transform** | перетащить `WordVisual` из Hierarchy |
| **Label** | перетащить `TextMeshProUGUI` с `WordVisual` |
| **Correct Particles** | перетащить `CorrectParticles` (или оставить пустым) |

#### 2.7 Сохранить как префаб

1. Перетащить `WordToken` из **Hierarchy** в папку `Assets/ReadingModule/Prefabs/`.
2. Удалить экземпляр из сцены.

---

### Шаг 3 — Настройка иерархии сцены

```
Canvas
├── SentenceContainer          ← контейнер для слов
├── PermissionPanel
├── DeniedPanel
└── MicIndicatorPanel

SpeechBridge                   ← пустой GameObject, имя СТРОГО "SpeechBridge"
ReadingController              ← пустой GameObject
MicrophonePermissionHandler    ← пустой GameObject
MicLevelIndicator              ← пустой GameObject
```

#### 3.1 Создать Canvas

1. **GameObject → UI → Canvas**.
2. Выбрать Canvas → **Canvas Scaler**:

   | Поле | Значение |
   |---|---|
   | **UI Scale Mode** | Scale With Screen Size |
   | **Reference Resolution** | X=1920, Y=1080 |
   | **Screen Match Mode** | Match Width Or Height |
   | **Match** | 0.5 |

#### 3.2 Создать SentenceContainer

1. Щёлкнуть правой кнопкой на Canvas → **UI → Empty**.
2. Переименовать в **`SentenceContainer`**.
3. **Rect Transform**:

   | Поле | Значение |
   |---|---|
   | **Anchors** | Min X=0.5, Min Y=0.5, Max X=0.5, Max Y=0.5 |
   | **Pivot** | X=0.5, Y=0.5 |
   | **Pos X / Pos Y** | 0, 0 |

4. **Add Component → Layout → Horizontal Layout Group**:

   | Поле | Значение |
   |---|---|
   | **Spacing** | 12 |
   | **Child Alignment** | Middle Center |
   | **Control Child Size** Width | ✗ **ON** |
   | **Control Child Size** Height | ✗ Off |
   | **Child Force Expand** Width | ✗ **OFF** |
   | **Child Force Expand** Height | ✗ Off |

5. **Add Component → Layout → Content Size Fitter**:

   | Поле | Значение |
   |---|---|
   | **Horizontal Fit** | **Preferred Size** |
   | **Vertical Fit** | **Preferred Size** |

---

### Шаг 4 — Настройка ReadingController

1. Создать пустой GameObject, назвать **`ReadingController`**.
2. **Add Component → Scripts → ReadingModule → Reading Controller**.
3. Заполнить поля:

   | Поле | Значение |
   |---|---|
   | **Word Token Prefab** | префаб `WordToken` |
   | **Word Token Container** | `SentenceContainer` |
   | **Entry Stagger Delay** | `0.05` |
   | **Wave Stagger Delay** | `0.06` |
   | **Post Wave Pause** | `0.5` |
   | **Language** | `ru-RU` |
   | **Fuzzy Match Threshold** | `0.70` |
   | **Accept Cooldown Sec** | `0.8` |

4. Добавить предложения в **Sentences** → **+**:

   ```
   Element 0
     Sentence Text    : Жил-был маленький волшебник по имени Лео.
     Voice Over       : [AudioClip или пусто]
     Objects To Unlock: [GameObjects с SetActive(false) в сцене]
   ```

---

### Шаг 5 — Настройка SpeechBridge

1. Создать пустой GameObject.
2. **Имя СТРОГО: `SpeechBridge`** (с заглавной S и B, без пробелов).
3. **Add Component → Scripts → ReadingModule → Speech Bridge**.

---

### Шаг 6 — Настройка MicrophonePermissionHandler

1. Создать пустой GameObject, назвать **`MicrophonePermissionHandler`**.
2. **Add Component → Scripts → ReadingModule → Microphone Permission Handler**.
3. Создать UI для запроса разрешения:

   ```
   Canvas
   └── PermissionPanel (Panel)
       ├── Text "Нажми кнопку и разреши доступ к микрофону!"
       └── AllowButton (Button)
   
   Canvas
   └── DeniedPanel (Panel, SetActive=false)
       ├── Text "Доступ запрещён..."
       └── RetryButton (Button, опционально)
   ```

4. В **On Permission Granted** → **+** дважды:
   - `ReadingController` → **`ReadingController.StartReading`**
   - `MicLevelIndicator` → **`MicLevelIndicator.StartMonitoring`**

---

### Шаг 7 — Настройка MicLevelIndicator

1. Создать пустой GameObject, назвать **`MicLevelIndicator`**.
2. **Add Component → Scripts → ReadingModule → Mic Level Indicator**.
3. Создать UI индикатора:

   ```
   Canvas
   └── MicIndicatorPanel
       ├── LevelBarTrack (Image — фон полоски)
       │   └── LevelBarFill (Image — Fill Method=Horizontal)
       ├── StatusLabel (TextMeshProUGUI)
       └── SilentWarningPanel (SetActive=false)
           └── SilentWarningLabel (TextMeshProUGUI)
   ```

---

### Шаг 8 — Подключение событий ReadingController

| Событие | Рекомендуемое подключение |
|---|---|
| **On Word Success** | Animator героя → `SetTrigger("Happy")` |
| **On Word Error** | Animator героя → `SetTrigger("Sad")` |
| **On Sentence Complete (int)** | Контроллер страниц → `OnSentenceComplete()` |
| **On All Sentences Complete** | Следующая фаза → `StartNextPhase()` |

---

## Настройка анимаций WordToken

| Секция | Поле | По умолчанию | Описание |
|---|---|---|---|
| **Entry Animation** | Entry Duration | `0.30` | Скорость влёта каждого слова |
| **Entry Animation** | Entry Y Offset | `-24` | Откуда влетает (пиксели) |
| **Idle Float** | Float Amplitude | `4` | Амплитуда покачивания |
| **Idle Float** | Float Duration | `1.15` | Полупериод покачивания (сек) |
| **Correct Animation** | Correct Punch | `0.35` | Сила scale punch при верном прочтении |
| **Correct Animation** | Correct Duration | `0.30` | Длительность punch |
| **Error Animation** | Shake Amplitude | `18` | Амплитуда тряски (пиксели) |
| **Error Animation** | Shake Duration | `0.40` | Длительность тряски |
| **Sentence Complete Wave** | Wave Punch | `0.20` | Сила punch финальной волны |
| **Sentence Complete Wave** | Wave Duration | `0.25` | Длительность punch волны |

---

## Тестирование в редакторе Unity

`SpeechBridge` предоставляет горячие клавиши в Play Mode:

| Клавиша | Действие |
|---|---|
| `M` | Симулировать выдачу разрешения на микрофон |
| `Space` | Симулировать **верно** прочитанное слово |
| `X` | Симулировать **неверно** прочитанное слово |

---

## Паттерн разблокировки фоновых объектов

Разместить фоновые объекты в сцене с `SetActive(false)`:

```
BackgroundPage1
├── SwampBackground     ← SetActive(false)
├── River               ← SetActive(false)
└── PathTrees           ← SetActive(false)
```

Перетащить каждый в `ReadingController → Sentences[0] → Objects To Unlock`.

---

## Публичный API

```csharp
// Подключить к MicrophonePermissionHandler.onPermissionGranted
readingController.StartReading();

// Перезапустить с первого предложения
readingController.RestartFromBeginning();

// Перейти к конкретному предложению (0-based)
readingController.JumpToSentence(2);

// Статический утилитарный метод
float score = ReadingController.FuzzyMatch("дом", "дома"); // → 0.75
```
