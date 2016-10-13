// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Internal
{
    internal class EngineFileUtilities
    {
        public static readonly bool s_msbuildEagerWildCardEvaluation =
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MsBuildSkipEagerWildCarddEvaluationRegexes"));

        // by default no wildcards are supported.
        private static List<Regex> s_wildCardSupportedRegexes = PopulateRegexFromEnvironment();

        /// <summary>
        /// Used for the purposes of evaluating an item specification. Given a filespec that may include wildcard characters * and
        /// ?, we translate it into an actual list of files. If the input filespec doesn't contain any wildcard characters, and it
        /// doesn't appear to point to an actual file on disk, then we just give back the input string as an array of length one,
        /// assuming that it wasn't really intended to be a filename (as items are not required to necessarily represent files).
        /// Any wildcards passed in that are unescaped will be treated as real wildcards.
        /// The "include" of items passed back from the filesystem will be returned canonically escaped.
        /// The ordering of the list returned is deterministic (it is sorted).
        /// Will never throw IO exceptions. If path is invalid, just returns filespec verbatim.
        /// </summary>
        /// <param name="directoryEscaped">The directory to evaluate, escaped.</param>
        /// <param name="filespecEscaped">The filespec to evaluate, escaped.</param>
        /// <returns>Array of file paths, unescaped.</returns>
        internal static string[] GetFileListUnescaped
            (
            string directoryEscaped,
            string filespecEscaped,
            bool forceEvaluate = false
            )

        {
            return GetFileList(directoryEscaped, filespecEscaped, false /* returnEscaped */, forceEvaluate);
        }

        /// <summary>
        /// Used for the purposes of evaluating an item specification. Given a filespec that may include wildcard characters * and
        /// ?, we translate it into an actual list of files. If the input filespec doesn't contain any wildcard characters, and it
        /// doesn't appear to point to an actual file on disk, then we just give back the input string as an array of length one,
        /// assuming that it wasn't really intended to be a filename (as items are not required to necessarily represent files).
        /// Any wildcards passed in that are unescaped will be treated as real wildcards.
        /// The "include" of items passed back from the filesystem will be returned canonically escaped.
        /// The ordering of the list returned is deterministic (it is sorted).
        /// Will never throw IO exceptions. If path is invalid, just returns filespec verbatim.
        /// </summary>
        /// <param name="directoryEscaped">The directory to evaluate, escaped.</param>
        /// <param name="filespecEscaped">The filespec to evaluate, escaped.</param>
        /// <returns>Array of file paths, escaped.</returns>
        internal static string[] GetFileListEscaped
            (
            string directoryEscaped,
            string filespecEscaped,
            bool forceEvaluate = false
            )
        {
            return GetFileList(directoryEscaped, filespecEscaped, true /* returnEscaped */, forceEvaluate);
        }

        private static readonly bool s_ShowExpandedWildCards =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSBUILDLOGEXPANDEDWILDCARDS"));

        /// <summary>
        /// Used for the purposes of evaluating an item specification. Given a filespec that may include wildcard characters * and
        /// ?, we translate it into an actual list of files. If the input filespec doesn't contain any wildcard characters, and it
        /// doesn't appear to point to an actual file on disk, then we just give back the input string as an array of length one,
        /// assuming that it wasn't really intended to be a filename (as items are not required to necessarily represent files).
        /// Any wildcards passed in that are unescaped will be treated as real wildcards.
        /// The "include" of items passed back from the filesystem will be returned canonically escaped.
        /// The ordering of the list returned is deterministic (it is sorted).
        /// Will never throw IO exceptions: if there is no match, returns the input verbatim.
        /// </summary>
        /// <param name="directoryEscaped">The directory to evaluate, escaped.</param>
        /// <param name="filespecEscaped">The filespec to evaluate, escaped.</param>
        /// <returns>Array of file paths.</returns>
        private static string[] GetFileList
            (
            string directoryEscaped,
            string filespecEscaped,
            bool returnEscaped,
            bool forceEvaluateWildCards
            )
        {
            ErrorUtilities.VerifyThrowInternalLength(filespecEscaped, "filespecEscaped");

            string[] fileList;

            bool containsEscapedWildcards = EscapingUtilities.ContainsEscapedWildcards(filespecEscaped);
            bool containsRealWildcards = FileMatcher.HasWildcards(filespecEscaped);

            if (containsEscapedWildcards && containsRealWildcards)
            {
                // Umm, this makes no sense.  The item's Include has both escaped wildcards and 
                // real wildcards.  What does he want us to do?  Go to the file system and find
                // files that literally have '*' in their filename?  Well, that's not going to 
                // happen because '*' is an illegal character to have in a filename.

                // Just return the original string.
                fileList = new string[] { returnEscaped ? filespecEscaped : EscapingUtilities.UnescapeAll(filespecEscaped) };
            }
            else if (s_msbuildEagerWildCardEvaluation && !forceEvaluateWildCards &&
                     IsRegexMatch(filespecEscaped))
            {
                fileList = new string[] { returnEscaped ? filespecEscaped : EscapingUtilities.UnescapeAll(filespecEscaped) };
            }
            else if (!containsEscapedWildcards && containsRealWildcards)
            {
                if (s_ShowExpandedWildCards)
                {
                    ErrorUtilities.DebugTraceMessage("Expanding wildcard for file spec {0}", filespecEscaped);
                }

                // Unescape before handing it to the filesystem.
                string directoryUnescaped = EscapingUtilities.UnescapeAll(directoryEscaped);
                string filespecUnescaped = EscapingUtilities.UnescapeAll(filespecEscaped);

                // Get the list of actual files which match the filespec.  Put
                // the list into a string array.  If the filespec started out
                // as a relative path, we will get back a bunch of relative paths.
                // If the filespec started out as an absolute path, we will get
                // back a bunch of absolute paths.
                fileList = FileMatcher.GetFiles(directoryUnescaped, filespecUnescaped);

                ErrorUtilities.VerifyThrow(fileList != null, "We must have a list of files here, even if it's empty.");

                // Before actually returning the file list, we sort them alphabetically.  This
                // provides a certain amount of extra determinism and reproducability.  That is,
                // we're sure that the build will behave in exactly the same way every time,
                // and on every machine.
                Array.Sort(fileList, StringComparer.OrdinalIgnoreCase);

                if (returnEscaped)
                {
                    // We must now go back and make sure all special characters are escaped because we always 
                    // store data in the engine in escaped form so it doesn't interfere with our parsing.
                    // Note that this means that characters that were not escaped in the original filespec
                    // may now be escaped, but that's not easy to avoid.
                    for (int i = 0; i < fileList.Length; i++)
                    {
                        fileList[i] = EscapingUtilities.Escape(fileList[i]);
                    }
                }
            }
            else
            {
                // No real wildcards means we just return the original string.  Don't even bother 
                // escaping ... it should already be escaped appropriately since it came directly
                // from the project file or the OM host.
                fileList = new string[] { returnEscaped ? filespecEscaped : EscapingUtilities.UnescapeAll(filespecEscaped) };
            }

            return fileList;
        }

        private static List<Regex> PopulateRegexFromEnvironment()
        {
            string wildCards = Environment.GetEnvironmentVariable("MsBuildSkipEagerWildCarddEvaluationRegexes");
            if (string.IsNullOrEmpty(wildCards))
            {
                return new List<Regex>(0);
            }
            else
            {
                List<Regex> regexes = new List<Regex>();
                foreach (string regex in wildCards.Split(';'))
                {
                    Regex item = new Regex(regex, RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    // trigger a match first?
                    item.IsMatch("foo");
                    regexes.Add(item);
                }

                return regexes;
            }
        }

        private static readonly ConcurrentDictionary<string, bool> _isRegexMatch = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal); 

        private static bool IsRegexMatch(string fileSpec)
        {
            return _isRegexMatch.GetOrAdd(fileSpec, file => s_wildCardSupportedRegexes.Any(regex => regex.IsMatch(fileSpec)));
        }
    }
}
