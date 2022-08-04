using Humanizer;

namespace FundsManager.Helpers
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
    }
}