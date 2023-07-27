/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using Humanizer;

namespace NodeGuard.Helpers
{
    public static class StringHelper
    {
        /// <summary>
        /// Truncates the string to show only numberOfCharactersToDisplay from head and tail and dots in the middle.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="numberOfCharactersToDisplay"></param>
        /// <returns></returns>
        public static string TruncateHeadAndTail(string str, int numberOfCharactersToDisplay)
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                return string.Empty;
            }

            var start = str.Substring(0, str.Length / 2);

            var end = str.Substring(str.Length / 2, str.Length - (str.Length / 2));
            var truncationString = "...";

            var truncationStringLength = numberOfCharactersToDisplay + truncationString.Length;
            var startTruncated = start.Truncate(truncationStringLength, truncationString, Truncator.FixedLength, TruncateFrom.Right);

            var endTruncated = end.Truncate(truncationStringLength, truncationString, Truncator.FixedLength, TruncateFrom.Left);

            var result = $"{startTruncated}{endTruncated}";

            return result;
        }

        public static bool IsTrue(string? str)
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                return false;
            }
            var lowerStr = str.ToLowerInvariant();
            return lowerStr == "true" || str == "1" || lowerStr == "yes";
        }
    }
}