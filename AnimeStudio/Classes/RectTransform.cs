using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnimeStudio
{
    public sealed class RectTransform : Transform
    {
        public Vector2 m_AnchorMin;
        public Vector2 m_AnchorMax;
        public Vector2 m_AnchoredPosition;
        public Vector2 m_SizeDelta;
        public Vector2 m_Pivot;

        public RectTransform(ObjectReader reader) : base(reader)
        {
            m_AnchorMin = reader.ReadVector2();
            m_AnchorMax = reader.ReadVector2();
            m_AnchoredPosition = reader.ReadVector2();
            m_SizeDelta = reader.ReadVector2();
            m_Pivot = reader.ReadVector2();
        }
    }
}
