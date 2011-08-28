﻿#region License
/* **********************************************************************************
 * Copyright (c) Michael Tindal
 * This source code is subject to terms and conditions of the MIT License
 * for Irony. A copy of the license can be found in the License.txt file
 * at the root of this distribution. 
 * By using this source code in any fashion, you are agreeing to be bound by the terms of the 
 * MIT License.
 * You must not remove this notice from this software.
 * **********************************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;

namespace Irony.Parsing {

    [Flags]
    public enum HereDocOptions {
        None = 0,
        AllowIndentedEndToken = 0x01,
    }

    public class HereDocTerminal : Terminal {

        #region HereDocSubType
        class HereDocSubType {
            internal readonly string Start;
            internal readonly HereDocOptions Flags;

            internal HereDocSubType(string start, HereDocOptions flags) {
                Start = start;
                Flags = flags;
            }

            internal static int LongerStartFirst(HereDocSubType x, HereDocSubType y) {
                try {//in case any of them is null
                    if (x.Start.Length > y.Start.Length) return -1;
                } catch { }
                return 0;
            }

            internal string GetTag(ParsingContext context, ISourceStream source) {
                char[] endOfTokenMarker = context.Language.GrammarData.WhitespaceAndDelimiters.ToCharArray();
                var endOfTagPos = source.Text.IndexOfAny(endOfTokenMarker, source.PreviewPosition + this.Start.Length);
                //We could not find the end of the token
                if (endOfTagPos == -1)
                    return null;
                source.PreviewPosition = endOfTagPos;
                var tagLength = source.PreviewPosition - source.Location.Position - this.Start.Length;
                return source.Text.Substring(source.PreviewPosition - tagLength, tagLength);
            }

            internal bool CheckEnd(ParsingContext context, ISourceStream source, string line, string tag) {
                if (this.Flags.HasFlag(HereDocOptions.AllowIndentedEndToken)) {
                    if (line.Trim().IndexOf(tag, context.Language.Grammar.CaseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase) == 0) {
                        return true;
                    }
                } else {
                    if (source.MatchSymbol(tag, !context.Language.Grammar.CaseSensitive)) {
                        return true;
                    }
                }
                return false;
            }
        }

        class HereDocSubTypeList : List<HereDocSubType> {
            internal void Add(string start, HereDocOptions flags) {
                base.Add(new HereDocSubType(start, flags));
            }
        }
        #endregion

        #region Constructors and Initialization
        public HereDocTerminal(string name)
            : base(name) {
            base.SetFlag(TermFlags.IsLiteral);
        }

        public HereDocTerminal(string name, string start, HereDocOptions options)
            : this(name) {
            _subtypes.Add(start, options);
        }

        public HereDocTerminal(string name, string start, HereDocOptions options, Terminal output)
            : this(name, start, options) {
            this.OutputTerminal = output;
        }

        public void AddSubType(string startSymbol, HereDocOptions hereDocOptions) {
            _subtypes.Add(startSymbol, hereDocOptions);
        }
        #endregion

        #region Private fields
        private readonly HereDocSubTypeList _subtypes = new HereDocSubTypeList();
        #endregion

        #region Private methods
        private HereDocSubType DetermineSubType(ParsingContext context, ISourceStream source) {
            foreach (HereDocSubType s in _subtypes) {
                if (source.MatchSymbol(s.Start, !context.Language.Grammar.CaseSensitive)) {
                    return s;
                }
            }
            return null;
        }

        private int GetNextPosition(ParsingContext context) {
            try {
                return (int)context.Values["HereDocNextPosition"];
            } catch { 
                return -1; 
            }
        }

        private void SetNextPosition(ParsingContext context, int position) {
            context.Values["HereDocNextPosition"] = position;
        }

        private void SetUpEvent(ParsingContext context) {
            char[] endOfLineMarker = context.Language.Grammar.LineTerminators.ToCharArray();
            if (!context.Values.ContainsKey("HereDocEventCreated")) {
                context.Language.Grammar.NewLine.ValidateToken += (sender, args) => {
                    int nextPosition = GetNextPosition(context);
                    if (nextPosition != -1) {
                        args.Context.Source.ForcePreviewPosition = true;
                        args.Context.Source.PreviewPosition = nextPosition;
                        if (args.Context.Source.Text[nextPosition] == '\n')
                            args.Context.Source.PreviewPosition++;
                    }
                };
                context.Values["HereDocEventCreated"] = true;
            }
        }
        #endregion

        #region Overrides and methods
        public override void Init(GrammarData grammarData) {
            base.Init(grammarData);
            _subtypes.Sort(HereDocSubType.LongerStartFirst);
        }

        public override IList<string> GetFirsts() {
            List<string> firsts = new List<string>();
            foreach (var subtype in _subtypes) {
                firsts.Add(subtype.Start);
            }
            return firsts;
        }

        public override Token TryMatch(ParsingContext context, ISourceStream source) {
            SetUpEvent(context);

            // First determine which subtype we are working with:
            HereDocSubType subtype = DetermineSubType(context, source);
            char[] endOfLineMarker = context.Language.Grammar.LineTerminators.ToCharArray();
            if (subtype == null) {
                return null;
            }
            //Find the end of the heredoc token
            var tag = subtype.GetTag(context, source);
            var endOfTagPos = source.PreviewPosition;
            if (tag == null)
                return source.CreateErrorToken(Resources.ErrNoEndForHeredoc);
            var endOfLinePos = source.Text.IndexOfAny(endOfLineMarker, source.PreviewPosition);
            if (endOfLinePos == -1)
                return source.CreateErrorToken(Resources.ErrNoEndForHeredoc);
            source.PreviewPosition = endOfLinePos;

            source.PreviewPosition++;
            if (GetNextPosition(context) != -1)
                source.PreviewPosition = GetNextPosition(context);

            var value = new StringBuilder();
            var endFound = false;
            while (!endFound) {
                var eolPos = source.Text.IndexOfAny(endOfLineMarker, source.PreviewPosition);
                if (eolPos == -1)
                    break;
                source.PreviewPosition = eolPos + 1;
                var nextEol = source.Text.IndexOfAny(endOfLineMarker, eolPos + 1);
                var line = nextEol == -1 ? source.Text.Substring(eolPos + 1) : source.Text.Substring(eolPos + 1, nextEol - eolPos - 1);

                endFound = subtype.CheckEnd(context, source, line, tag);

                if (!endFound) value.AppendLine(line);

                if (nextEol != -1) {
                    SetNextPosition(context, nextEol + 1);

                    source.PreviewPosition = nextEol + 1;
                }
            }
            if (endFound) {
                Token token = source.CreateToken(this.OutputTerminal);
                token.Value = value.ToString();
                if (source.Text.IndexOfAny(endOfLineMarker, endOfTagPos) != endOfTagPos) {
                    source.ForcePreviewPosition = true;
                    source.PreviewPosition = endOfTagPos;
                }  else
                    SetNextPosition(context, -1);
                return token;
            } else
                return source.CreateErrorToken(Resources.ErrNoEndForHeredoc);
        }
        #endregion
    }
}
