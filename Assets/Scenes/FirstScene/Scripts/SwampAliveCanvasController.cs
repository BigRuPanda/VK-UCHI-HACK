using UnityEngine;
using UnityEngine.UI;

public class SwampAliveCanvasFX : MonoBehaviour
{
    class Item
    {
        public RectTransform rt;
        public Image img;
        public Vector2 startPos;
        public Vector3 startScale;
        public float offset;
        public int type; // 0=fog, 1=glow, 2=firefly
    }

    private Item[] items;

    // настройки
    public float fogSpeed = 0.3f;
    public float glowPulseSpeed = 2.5f;
    public float fireflySpeed = 1.2f;

    void Awake()
    {
        var rects = GetComponentsInChildren<RectTransform>(true);
        items = new Item[rects.Length - 1];

        int i = 0;

        foreach (var rt in rects)
        {
            if (rt == transform as RectTransform)
                continue;

            var it = new Item();
            it.rt = rt;
            it.img = rt.GetComponent<Image>();
            it.startPos = rt.anchoredPosition;
            it.startScale = rt.localScale;
            it.offset = Random.Range(0f, 100f);

            string name = rt.name.ToLower();

            if (name.Contains("fog"))
                it.type = 0;
            else if (name.Contains("glow"))
                it.type = 1;
            else
                it.type = 2; // всё остальное считаем firefly

            items[i++] = it;
        }
    }

    void Update()
    {
        float t = Time.time;

        foreach (var it in items)
        {
            if (it.rt == null) continue;

            float time = t + it.offset;

            switch (it.type)
            {
                // 🌫 ТУМАН — медленно плывёт
                case 0:
                {
                    float x = Mathf.Sin(time * fogSpeed) * 40f;
                    it.rt.anchoredPosition = it.startPos + new Vector2(x, 0);
                    break;
                }

                case 1:
                {
                    float s = 1f + Mathf.Sin(time * glowPulseSpeed) * 0.08f;
                    it.rt.localScale = it.startScale * s;

                    if (it.img != null)
                    {
                        float pulse = (Mathf.Sin(time * glowPulseSpeed) + 1f) * 0.5f;

                        Color c = it.img.color;

                        // 🔥 чуть сильнее альфа
                        c.a = Mathf.Lerp(0.25f, 0.6f, pulse);

                        it.img.color = c;
                    }

                    break;
                }

                // 🐛 ОГОНЬКИ — плавают + булькают + исчезают
                case 2:
                {
                    float y = Mathf.Sin(time * fireflySpeed) * 20f;
                    float x = Mathf.Cos(time * fireflySpeed * 0.6f) * 10f;

                    it.rt.anchoredPosition = it.startPos + new Vector2(x, y);

                    float s = 1f + Mathf.Sin(time * 3f) * 0.3f;
                    it.rt.localScale = it.startScale * s;

                    if (it.img != null)
                    {
                        float alpha = (Mathf.Sin(time * 1.5f) + 1f) * 0.5f;
                        Color c = it.img.color;
                        c.a = Mathf.Lerp(0.1f, 1f, alpha);
                        it.img.color = c;
                    }

                    break;
                }
            }
        }
    }
}