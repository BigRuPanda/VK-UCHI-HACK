PIRATE SCENE UNITY PACK

Что внутри:
- Assets/ — картинки, которые ты скинул.
- Scripts/PirateSceneAnimator.cs — анимация корабля, волн, птиц и облаков без Animator Controller.
- Scripts/StartSequence_WithPirateScene.cs — твоя цепочка: панель исчезает, герой идёт, светлячки выключаются, книга закрывается, потом можно включить пиратскую сцену.

Как собрать в Unity:
1. Закинь папку PirateSceneUnityPack в Assets проекта.
2. Создай пустой объект PirateSceneRoot.
3. Внутри сделай объекты:
   - Background
   - Ship
   - Waves
   - Clouds/Sky
   - Birds
4. На каждый объект добавь Image, если это Canvas UI, или SpriteRenderer, если это обычная 2D сцена.
5. Назначь соответствующие PNG.
6. На PirateSceneRoot добавь скрипт PirateSceneAnimator.
7. Перетащи объекты в поля:
   - ship = Ship
   - waves = Waves
   - birds = Birds
   - clouds = Clouds/Sky
   - background = Background
8. Если хочешь плавное появление через прозрачность, добавь CanvasGroup на Ship/Waves/Birds/Clouds и перетащи их в поля CanvasGroup.
9. Play On Start выключен по умолчанию. Сцена не начнет анимацию сама.
10. Чтобы запустить вручную, вызови PirateSceneAnimator.PlaySceneAnimation() из UnityEvent.

Если используешь StartSequence_WithPirateScene:
1. Повесь StartSequence_WithPirateScene на любой объект-сценарий.
2. В UnityEvent выполненного задания выбери StartSequence_WithPirateScene -> PlaySequence().
3. Заполни поля:
   - panelToDisable = DrawPanel
   - panelAnimator = Animator у DrawPanel
   - panelHideTrigger = Hide
   - heroAnimator = Animator героя
   - heroTrigger = StartWalk
   - swampFX = светлячки
   - bookAnimator = Animator книги
   - bookTrigger = BookClose
   - pirateSceneRoot = PirateSceneRoot
   - pirateSceneAnimator = компонент PirateSceneAnimator на PirateSceneRoot

Важно:
- Если PirateSceneRoot не должен быть виден в начале, выключи его галочку в Hierarchy.
- StartSequence_WithPirateScene сам включит pirateSceneRoot после книги.
- Если не нужна пиратская сцена после книги, просто оставь pirateSceneRoot и pirateSceneAnimator пустыми.

Рекомендованные значения:
- panelHideAnimationDuration = длина анимации исчезновения панели
- heroAnimationDuration = длина анимации героя
- shipBobHeight = 10-20
- shipRockAngle = 2-4
- wavesMoveX = 50-100
