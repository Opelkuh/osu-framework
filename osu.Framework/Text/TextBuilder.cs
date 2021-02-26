// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Caching;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace osu.Framework.Text
{
    /// <summary>
    /// A text builder for <see cref="SpriteText"/> and other text-based display components.
    /// </summary>
    public class TextBuilder
    {
        /// <summary>
        /// The bounding size of the composite text.
        /// </summary>
        public Vector2 Bounds { get; private set; }

        /// <summary>
        /// The characters generated by this <see cref="TextBuilder"/>.
        /// </summary>
        public readonly List<TextBuilderGlyph> Characters;

        private readonly char[] neverFixedWidthCharacters;
        private readonly char fallbackCharacter;
        private readonly char fixedWidthReferenceCharacter;
        private readonly ITexturedGlyphLookupStore store;
        private readonly FontUsage font;
        private readonly bool useFontSizeAsHeight;
        private readonly Vector2 startOffset;
        private readonly Vector2 spacing;
        private readonly float maxWidth;

        private Vector2 currentPos;
        private float currentLineHeight;
        private bool currentNewLine = true;

        /// <summary>
        /// Creates a new <see cref="TextBuilder"/>.
        /// </summary>
        /// <param name="store">The store from which glyphs are to be retrieved from.</param>
        /// <param name="font">The font to use for glyph lookups from <paramref name="store"/>.</param>
        /// <param name="useFontSizeAsHeight">True to use the provided <see cref="font"/> size as the height for each line. False if the height of each individual glyph should be used.</param>
        /// <param name="startOffset">The offset at which characters should begin being added at.</param>
        /// <param name="spacing">The spacing between characters.</param>
        /// <param name="maxWidth">The maximum width of the resulting text bounds.</param>
        /// <param name="characterList">That list to contain all resulting <see cref="TextBuilderGlyph"/>s.</param>
        /// <param name="neverFixedWidthCharacters">The characters for which fixed width should never be applied.</param>
        /// <param name="fallbackCharacter">The character to use if a glyph lookup fails.</param>
        /// <param name="fixedWidthReferenceCharacter">The character to use to calculate the fixed width width. Defaults to 'm'.</param>
        public TextBuilder(ITexturedGlyphLookupStore store, FontUsage font, float maxWidth = float.MaxValue, bool useFontSizeAsHeight = true, Vector2 startOffset = default, Vector2 spacing = default,
                           List<TextBuilderGlyph> characterList = null, char[] neverFixedWidthCharacters = null, char fallbackCharacter = '?', char fixedWidthReferenceCharacter = 'm')
        {
            this.store = store;
            this.font = font;
            this.useFontSizeAsHeight = useFontSizeAsHeight;
            this.startOffset = startOffset;
            this.spacing = spacing;
            this.maxWidth = maxWidth;

            Characters = characterList ?? new List<TextBuilderGlyph>();
            this.neverFixedWidthCharacters = neverFixedWidthCharacters ?? Array.Empty<char>();
            this.fallbackCharacter = fallbackCharacter;
            this.fixedWidthReferenceCharacter = fixedWidthReferenceCharacter;

            currentPos = startOffset;
        }

        /// <summary>
        /// Resets this <see cref="TextBuilder"/> to a default state.
        /// </summary>
        public virtual void Reset()
        {
            Bounds = Vector2.Zero;
            Characters.Clear();

            currentPos = startOffset;
            currentLineHeight = 0;
            currentNewLine = true;
        }

        /// <summary>
        /// Whether characters can be added to this <see cref="TextBuilder"/>.
        /// </summary>
        protected virtual bool CanAddCharacters => true;

        /// <summary>
        /// Appends text to this <see cref="TextBuilder"/>.
        /// </summary>
        /// <param name="text">The text to append.</param>
        public void AddText(string text)
        {
            foreach (var c in text)
            {
                if (!AddCharacter(c))
                    break;
            }
        }

        /// <summary>
        /// Appends a character to this <see cref="TextBuilder"/>.
        /// </summary>
        /// <param name="character">The character to append.</param>
        /// <returns>Whether characters can still be added.</returns>
        public bool AddCharacter(char character)
        {
            if (!CanAddCharacters)
                return false;

            if (!tryCreateGlyph(character, out var glyph))
                return true;

            // For each character that is added:
            // 1. Add the kerning to the current position if required.
            // 2. Draw the character at the current position offset by the glyph.
            //    The offset is not applied to the current position, it is only a value to be used at draw-time.
            // 3. Advance the current position by glyph's XAdvance.

            float kerning = 0;

            // Spacing + kerning are only applied if the current line is not empty
            if (!currentNewLine)
            {
                if (Characters.Count > 0)
                    kerning = glyph.GetKerning(Characters[^1].Glyph);
                kerning += spacing.X;
            }

            // Check if there is enough space for the character and let subclasses decide whether to continue adding the character if not
            if (!HasAvailableSpace(kerning + glyph.XAdvance))
            {
                OnWidthExceeded();

                if (!CanAddCharacters)
                    return false;
            }

            // The kerning is only added after it is guaranteed that the character will be added, to not leave the current position in a bad state
            currentPos.X += kerning;

            glyph.DrawRectangle = new RectangleF(new Vector2(currentPos.X + glyph.XOffset, currentPos.Y + glyph.YOffset), new Vector2(glyph.Width, glyph.Height));
            glyph.OnNewLine = currentNewLine;
            Characters.Add(glyph);

            currentPos.X += glyph.XAdvance;
            currentLineHeight = Math.Max(currentLineHeight, getGlyphHeight(ref glyph));
            currentNewLine = false;

            Bounds = Vector2.ComponentMax(Bounds, currentPos + new Vector2(0, currentLineHeight));
            return true;
        }

        /// <summary>
        /// Adds a new line to this <see cref="TextBuilder"/>.
        /// </summary>
        /// <remarks>
        /// A height equal to that of the font size will be assumed if the current line is empty, regardless of <see cref="useFontSizeAsHeight"/>.
        /// </remarks>
        public void AddNewLine()
        {
            if (currentNewLine)
                currentLineHeight = font.Size;

            // Reset + vertically offset the current position
            currentPos.X = startOffset.X;
            currentPos.Y += currentLineHeight + spacing.Y;

            currentLineHeight = 0;
            currentNewLine = true;
        }

        /// <summary>
        /// Removes the last character added to this <see cref="TextBuilder"/>.
        /// If the character is the first character on a new line, the new line is also removed.
        /// </summary>
        public void RemoveLastCharacter()
        {
            if (Characters.Count == 0)
                return;

            TextBuilderGlyph removedCharacter = Characters[^1];
            TextBuilderGlyph? previousCharacter = Characters.Count == 1 ? null : (TextBuilderGlyph?)Characters[^2];

            Characters.RemoveAt(Characters.Count - 1);

            // For each character that is removed:
            // 1. Calculate the line height of the last line.
            // 2. If the character is the first on a new line, move the current position upwards by the calculated line height and to the end of the previous line.
            //    The position at the end of the line is the post-XAdvanced position.
            // 3. If the character is not the first on a new line, move the current position backwards by the XAdvance and the kerning from the previous glyph.
            //    This brings the current position to the post-XAdvanced position of the previous glyph.

            currentLineHeight = 0;

            // This is O(n^2) for removing all characters within a line, but is generally not used in such a case
            for (int i = Characters.Count - 1; i >= 0; i--)
            {
                var character = Characters[i];

                currentLineHeight = Math.Max(currentLineHeight, getGlyphHeight(ref character));

                if (character.OnNewLine)
                    break;
            }

            if (removedCharacter.OnNewLine)
            {
                // Move up to the previous line
                currentPos.Y -= currentLineHeight;

                // If this is the first line (ie. there are no characters remaining) we shouldn't be removing the spacing,
                // as there is no spacing applied to the first line.
                if (Characters.Count > 0)
                    currentPos.Y -= spacing.Y;

                currentPos.X = 0;

                if (previousCharacter != null)
                {
                    // The character's draw rectangle is the only marker that keeps a constant state for the position, but it has the glyph's XOffset added into it
                    // So the post-kerned position can be retrieved by taking the XOffset away, and the post-XAdvanced position is retrieved by adding the XAdvance back in
                    currentPos.X = previousCharacter.Value.DrawRectangle.Left - previousCharacter.Value.XOffset + previousCharacter.Value.XAdvance;
                }
            }
            else
            {
                // Move back within the current line, reversing the operations in AddCharacter()
                currentPos.X -= removedCharacter.XAdvance;

                if (previousCharacter != null)
                    currentPos.X -= removedCharacter.GetKerning(previousCharacter.Value) + spacing.X;
            }

            Bounds = Vector2.Zero;

            for (int i = 0; i < Characters.Count; i++)
            {
                // As above, the bounds are calculated through the character draw rectangles
                Bounds = Vector2.ComponentMax(
                    Bounds,
                    new Vector2(
                        Characters[i].DrawRectangle.Left - Characters[i].XOffset + Characters[i].XAdvance,
                        Characters[i].DrawRectangle.Top - Characters[i].YOffset + currentLineHeight)
                );
            }

            // The new line is removed when the first character on the line is removed, thus the current position is never on a new line
            // after a character is removed except when there are no characters remaining in the builder
            if (Characters.Count == 0)
                currentNewLine = true;
        }

        /// <summary>
        /// Invoked when a character is being added that exceeds the maximum width of the text bounds.
        /// </summary>
        /// <remarks>
        /// The character will not continue being added if <see cref="CanAddCharacters"/> is changed during this invocation.
        /// </remarks>
        protected virtual void OnWidthExceeded()
        {
        }

        /// <summary>
        /// Whether there is enough space in the available text bounds.
        /// </summary>
        /// <param name="length">The space requested.</param>
        protected virtual bool HasAvailableSpace(float length) => currentPos.X + length <= maxWidth;

        /// <summary>
        /// Retrieves the height of a glyph.
        /// </summary>
        /// <param name="glyph">The glyph to retrieve the height of.</param>
        /// <returns>The height of the glyph.</returns>
        private float getGlyphHeight<T>(ref T glyph)
            where T : ITexturedCharacterGlyph
        {
            if (useFontSizeAsHeight)
                return font.Size;

            // Space characters typically have heights that exceed the height of all other characters in the font
            // Thus, the height is forced to 0 such that only non-whitespace character heights are considered
            if (glyph.IsWhiteSpace())
                return 0;

            return glyph.YOffset + glyph.Height;
        }

        private readonly Cached<float> constantWidthCache = new Cached<float>();

        private float getConstantWidth() => constantWidthCache.IsValid ? constantWidthCache.Value : constantWidthCache.Value = getTexturedGlyph(fixedWidthReferenceCharacter)?.Width ?? 0;

        private bool tryCreateGlyph(char character, out TextBuilderGlyph glyph)
        {
            var fontStoreGlyph = getTexturedGlyph(character);

            if (fontStoreGlyph == null)
            {
                glyph = default;
                return false;
            }

            // Array.IndexOf is used to avoid LINQ
            if (font.FixedWidth && Array.IndexOf(neverFixedWidthCharacters, character) == -1)
                glyph = new TextBuilderGlyph(fontStoreGlyph, font.Size, getConstantWidth());
            else
                glyph = new TextBuilderGlyph(fontStoreGlyph, font.Size);

            return true;
        }

        private ITexturedCharacterGlyph getTexturedGlyph(char character)
        {
            return store.Get(font.FontName, character)
                   ?? store.Get(null, character)
                   ?? store.Get(font.FontName, fallbackCharacter)
                   ?? store.Get(null, fallbackCharacter);
        }
    }
}
