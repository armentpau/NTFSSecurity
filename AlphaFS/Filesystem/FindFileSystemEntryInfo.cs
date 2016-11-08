/*  Copyright (C) 2008-2015 Peter Palotas, Jeffrey Jangli, Alexandr Normuradov
 *  
 *  Permission is hereby granted, free of charge, to any person obtaining a copy 
 *  of this software and associated documentation files (the "Software"), to deal 
 *  in the Software without restriction, including without limitation the rights 
 *  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 *  copies of the Software, and to permit persons to whom the Software is 
 *  furnished to do so, subject to the following conditions:
 *  
 *  The above copyright notice and this permission notice shall be included in 
 *  all copies or substantial portions of the Software.
 *  
 *  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
 *  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN 
 *  THE SOFTWARE. 
 */

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;

namespace Alphaleonis.Win32.Filesystem
{
   /// <summary>Class that retrieves file system entries (i.e. files and directories) using Win32 API FindFirst()/FindNext().</summary>
   [SerializableAttribute]
   internal sealed class FindFileSystemEntryInfo
   {
      #region Constructor

      public FindFileSystemEntryInfo(bool isFolder, KernelTransaction transaction, string path, string searchPattern, DirectoryEnumerationOptions options, Type typeOfT, PathFormat pathFormat)
      {
         Transaction = transaction;

         InputPath = Path.GetExtendedLengthPathCore(transaction, path, pathFormat, GetFullPathOptions.RemoveTrailingDirectorySeparator | GetFullPathOptions.FullCheck);

         SearchPattern = searchPattern;
         FileSystemObjectType = null;

         ContinueOnException = (options & DirectoryEnumerationOptions.ContinueOnException) != 0;

         AsLongPath = (options & DirectoryEnumerationOptions.AsLongPath) != 0;

         AsString = typeOfT == typeof(string);
         AsFileSystemInfo = !AsString && (typeOfT == typeof(FileSystemInfo) || typeOfT.BaseType == typeof(FileSystemInfo));

         FindExInfoLevel = NativeMethods.IsAtLeastWindows7 && (options & DirectoryEnumerationOptions.BasicSearch) != 0
            ? NativeMethods.FINDEX_INFO_LEVELS.Basic
            : NativeMethods.FINDEX_INFO_LEVELS.Standard;

         LargeCache = NativeMethods.IsAtLeastWindows7 && (options & DirectoryEnumerationOptions.LargeCache) != 0
            ? NativeMethods.FindExAdditionalFlags.LargeFetch
            : NativeMethods.FindExAdditionalFlags.None;

         IsDirectory = isFolder;

         if (IsDirectory)
         {
            // Need files or folders to enumerate.
            if ((options & DirectoryEnumerationOptions.FilesAndFolders) == DirectoryEnumerationOptions.None)
               options |= DirectoryEnumerationOptions.FilesAndFolders;

            FileSystemObjectType = (options & DirectoryEnumerationOptions.FilesAndFolders) == DirectoryEnumerationOptions.FilesAndFolders
               ? (bool?)null
               : (options & DirectoryEnumerationOptions.Folders) != 0;

            Recursive = (options & DirectoryEnumerationOptions.Recursive) != 0;

            SkipReparsePoints = (options & DirectoryEnumerationOptions.SkipReparsePoints) != 0;
         }
      }

      #endregion // Constructor

      #region Methods

      #region FindFirstFile

      private SafeFindFileHandle FindFirstFile(string pathLp, out NativeMethods.WIN32_FIND_DATA win32FindData)
      {
         SafeFindFileHandle handle = Transaction == null || !NativeMethods.IsAtLeastWindowsVista

            // FindFirstFileEx() / FindFirstFileTransacted()
            // In the ANSI version of this function, the name is limited to MAX_PATH characters.
            // To extend this limit to 32,767 wide characters, call the Unicode version of the function and prepend "\\?\" to the path.
            // 2013-01-13: MSDN confirms LongPath usage.

            // A trailing backslash is not allowed.
            ? NativeMethods.FindFirstFileEx(Path.RemoveTrailingDirectorySeparator(pathLp, false), FindExInfoLevel, out win32FindData, _limitSearchToDirs, IntPtr.Zero, LargeCache)
            : NativeMethods.FindFirstFileTransacted(Path.RemoveTrailingDirectorySeparator(pathLp, false), FindExInfoLevel, out win32FindData, _limitSearchToDirs, IntPtr.Zero, LargeCache, Transaction.SafeHandle);

         int lastError = Marshal.GetLastWin32Error();


         if (handle != null && handle.IsInvalid)
         {
            handle.Close();

            if (!ContinueOnException)
            {
               switch ((uint) lastError)
               {
                  case Win32Errors.ERROR_FILE_NOT_FOUND:
                  case Win32Errors.ERROR_PATH_NOT_FOUND:
                     // MSDN: .NET 3.5+: DirectoryNotFoundException: Path is invalid, such as referring to an unmapped drive.
                     // Directory.Delete()

                     NativeError.ThrowException(IsDirectory ? (int) Win32Errors.ERROR_PATH_NOT_FOUND : Win32Errors.ERROR_FILE_NOT_FOUND, pathLp);
                     break;

                  case Win32Errors.ERROR_DIRECTORY:
                     // MSDN: .NET 3.5+: IOException: path is a file name.
                     // Directory.EnumerateDirectories()
                     // Directory.EnumerateFiles()
                     // Directory.EnumerateFileSystemEntries()
                     // Directory.GetDirectories()
                     // Directory.GetFiles()
                     // Directory.GetFileSystemEntries()

                     NativeError.ThrowException(lastError, pathLp);
                     break;

                  case Win32Errors.ERROR_ACCESS_DENIED:
                     // MSDN: .NET 3.5+: UnauthorizedAccessException: The caller does not have the required permission.
                     NativeError.ThrowException(lastError, pathLp);
                     break;
               }

               // MSDN: .NET 3.5+: IOException
               NativeError.ThrowException(lastError, pathLp);
            }
         }

         return handle;
      }

      #endregion // FindFirstFile

      #region NewFileSystemEntryType

      private T NewFileSystemEntryType<T>(NativeMethods.WIN32_FIND_DATA win32FindData, string fullPathLp, bool isFolder)
      {
         // Determine yield.
         if (FileSystemObjectType != null && ((!(bool) FileSystemObjectType || !isFolder) && (!(bool) !FileSystemObjectType || isFolder)))
            return (T) (object) null;

         if (AsString)
            // Return object instance FullPath property as string, optionally in long path format.
            return (T) (object) (AsLongPath ? fullPathLp : Path.GetRegularPathCore(fullPathLp, GetFullPathOptions.None));


         // Make sure the requested file system object type is returned.
         // null = Return files and directories.
         // true = Return only directories.
         // false = Return only files.

         var fsei = new FileSystemEntryInfo(win32FindData) { FullPath = fullPathLp };

         return AsFileSystemInfo
            // Return object instance of type FileSystemInfo.
            ? (T) (object) (fsei.IsDirectory
               ? (FileSystemInfo)
                  new DirectoryInfo(Transaction, fsei.LongFullPath, PathFormat.LongFullPath) {EntryInfo = fsei}
               : new FileInfo(Transaction, fsei.LongFullPath, PathFormat.LongFullPath) {EntryInfo = fsei})

            // Return object instance of type FileSystemEntryInfo.
            : (T) (object) fsei;

      }

      #endregion // NewFileSystemEntryType


      #region Enumerate

      /// <summary>Get an enumerator that returns all of the file system objects that match the wildcards that are in any of the directories to be searched.</summary>
      /// <returns>An <see cref="IEnumerable{T}"/> instance: FileSystemEntryInfo, DirectoryInfo, FileInfo or string (full path).</returns>
      [SecurityCritical]
      public IEnumerable<T> Enumerate<T>()
      {
         // MSDN: Queue
         // Represents a first-in, first-out collection of objects.
         // The capacity of a Queue is the number of elements the Queue can hold.
         // As elements are added to a Queue, the capacity is automatically increased as required through reallocation. The capacity can be decreased by calling TrimToSize.
         // The growth factor is the number by which the current capacity is multiplied when a greater capacity is required. The growth factor is determined when the Queue is constructed.
         // The capacity of the Queue will always increase by a minimum value, regardless of the growth factor; a growth factor of 1.0 will not prevent the Queue from increasing in size.
         // If the size of the collection can be estimated, specifying the initial capacity eliminates the need to perform a number of resizing operations while adding elements to the Queue.
         // This constructor is an O(n) operation, where n is capacity.

         var dirs = new Queue<string>(1000);

         // Removes the object at the beginning of your Queue.
         // The algorithmic complexity of this is O(1). It doesn't loop over elements.
         dirs.Enqueue(InputPath);

         // ChangeErrorMode is for the Win32 SetThreadErrorMode() method, used to suppress possible pop-ups.
         using (new NativeMethods.ChangeErrorMode(NativeMethods.ErrorMode.FailCriticalErrors))
            while (dirs.Count > 0)
            {
               string path = Path.AddTrailingDirectorySeparator(dirs.Dequeue(), false);
               string pathLp = path + Path.WildcardStarMatchAll;
               NativeMethods.WIN32_FIND_DATA win32FindData;

               using (SafeFindFileHandle handle = FindFirstFile(pathLp, out win32FindData))
               {
                  if (handle != null && handle.IsInvalid && ContinueOnException)
                  {
                     handle.Close();
                     continue;
                  }

                  do
                  {
                     string fileName = win32FindData.cFileName;

                     // Skip entries "." and ".."
                     if (fileName.Equals(Path.CurrentDirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
                         fileName.Equals(Path.ParentDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                     // Skip reparse points here to cleanly separate regular directories from links.
                     if (SkipReparsePoints && (win32FindData.dwFileAttributes & FileAttributes.ReparsePoint) != 0)
                        continue;


                     string fseiFullPathLp = path + fileName;
                     bool fseiIsFolder = (win32FindData.dwFileAttributes & FileAttributes.Directory) != 0;

                     // If object is a directory, add it to the queue for later traversal.
                     if (fseiIsFolder && Recursive)
                        dirs.Enqueue(fseiFullPathLp);

                     
                     // Determine yield.
                     if (!(_nameFilter == null || (_nameFilter != null && _nameFilter.IsMatch(fileName))))
                        continue;

                     var res = NewFileSystemEntryType<T>(win32FindData, fseiFullPathLp, fseiIsFolder);
                     if (res == null)
                        continue;

                     yield return res;

                  } while (NativeMethods.FindNextFile(handle, out win32FindData));


                  var lastError = (uint) Marshal.GetLastWin32Error();

                  if (!ContinueOnException)
                  {
                     switch (lastError)
                     {
                        case Win32Errors.ERROR_NO_MORE_FILES:
                           lastError = Win32Errors.NO_ERROR;
                           break;

                        case Win32Errors.ERROR_FILE_NOT_FOUND:
                        case Win32Errors.ERROR_PATH_NOT_FOUND:
                           if (lastError == Win32Errors.ERROR_FILE_NOT_FOUND && IsDirectory)
                              lastError = Win32Errors.ERROR_PATH_NOT_FOUND;
                           break;
                     }

                     if (lastError != Win32Errors.NO_ERROR)
                        NativeError.ThrowException(lastError, pathLp);
                  }
               }
            }
      }

      #endregion // Enumerate

      #region Get

      /// <summary>Gets a specific file system object.</summary>
      /// <returns>
      /// <para>The return type is based on C# inference. Possible return types are:</para>
      /// <para> <see cref="string"/>- (full path), <see cref="FileSystemInfo"/>- (<see cref="DirectoryInfo"/> or <see cref="FileInfo"/>), <see cref="FileSystemEntryInfo"/> instance</para>
      /// <para>or null in case an Exception is raised and <see cref="ContinueOnException"/> is <see langword="true"/>.</para>
      /// </returns>
      [SecurityCritical]
      public T Get<T>()
      {
         NativeMethods.WIN32_FIND_DATA win32FindData;

         // ChangeErrorMode is for the Win32 SetThreadErrorMode() method, used to suppress possible pop-ups.
         using (new NativeMethods.ChangeErrorMode(NativeMethods.ErrorMode.FailCriticalErrors))
         using (SafeFindFileHandle handle = FindFirstFile(InputPath, out win32FindData))
         {
            if (handle != null && !handle.IsInvalid)
               return NewFileSystemEntryType<T>(win32FindData, InputPath, (win32FindData.dwFileAttributes & FileAttributes.Directory) != 0);
         }

         return (T) (object) null;
      }

      #endregion // Get

      #endregion // Methods

      #region Properties

      #region AsFileSystemInfo

      /// <summary>Gets or sets the ability to return the object as a <see cref="FileSystemInfo"/> instance.</summary>
      /// <value><see langword="true"/> returns the object as a <see cref="FileSystemInfo"/> instance.</value>
      public bool AsFileSystemInfo { get; internal set; }

      #endregion // AsFileSystemInfo

      #region AsLongPath

      /// <summary>Gets or sets the ability to return the full path in long full path format.</summary>
      /// <value><see langword="true"/> returns the full path in long full path format, <see langword="false"/> returns the full path in regular path format.</value>
      public bool AsLongPath { get; internal set; }

      #endregion // AsLongPath

      #region AsString

      /// <summary>Gets or sets the ability to return the object instance as a <see cref="string"/>.</summary>
      /// <value><see langword="true"/> returns the full path of the object as a <see cref="string"/></value>
      public bool AsString { get; internal set; }

      #endregion // AsString

      #region FindExInfoLevel

      /// <summary>Gets the value indicating which <see cref="NativeMethods.FINDEX_INFO_LEVELS"/> to use.</summary>
      public NativeMethods.FINDEX_INFO_LEVELS FindExInfoLevel { get; internal set; }

      #endregion // FindExInfoLevel

      #region ContinueOnException

      /// <summary>Gets or sets the ability to skip on access errors.</summary>
      /// <value><see langword="true"/> suppress any Exception that might be thrown a result from a failure, such as ACLs protected directories or non-accessible reparse points.</value>
      public bool ContinueOnException { get; internal set; }

      #endregion // ContinueOnException

      #region FileSystemObjectType

      private NativeMethods.FINDEX_SEARCH_OPS _limitSearchToDirs = NativeMethods.FINDEX_SEARCH_OPS.SearchNameMatch;
      private bool? _fileSystemObjectType;

      /// <summary>Gets the file system object type.</summary>
      /// <value>
      /// <see langword="null"/> = Return files and directories.
      /// <see langword="true"/> = Return only directories.
      /// <see langword="false"/> = Return only files.
      /// </value>
      public bool? FileSystemObjectType
      {
         get { return _fileSystemObjectType; }

         private set
         {
            _fileSystemObjectType = value;

            _limitSearchToDirs = value != null && (bool)value
               ? NativeMethods.FINDEX_SEARCH_OPS.SearchLimitToDirectories
               : NativeMethods.FINDEX_SEARCH_OPS.SearchNameMatch;
         }
      }

      #endregion // FileSystemObjectType

      #region InputPath

      /// <summary>Gets or sets the path to the folder.</summary>
      /// <value>The path to the file or folder in long path format.</value>
      public string InputPath { get; internal set; }

      #endregion // InputPath

      #region IsDirectory

      /// <summary>Gets or sets a value indicating which <see cref="NativeMethods.FINDEX_INFO_LEVELS"/> to use.</summary>
      /// <value><see langword="true"/> indicates a folder object, <see langword="false"/> indicates a file object.</value>
      public bool IsDirectory { get; internal set; }

      #endregion // IsDirectory

      #region LargeCache

      /// <summary>Gets the value indicating which <see cref="NativeMethods.FindExAdditionalFlags"/> to use.</summary>
      public NativeMethods.FindExAdditionalFlags LargeCache { get; internal set; }

      #endregion // Cache

      #region Recursive

      /// <summary>Specifies whether the search should include only the current directory or should include all subdirectories.</summary>
      /// <value><see langword="true"/> to all subdirectories.</value>
      public bool Recursive { get; internal set; }

      #endregion // Recursive

      #region SearchPattern

      private string _searchPattern = Path.WildcardStarMatchAll;
      private Regex _nameFilter;

      /// <summary>Search for file system object-name using a pattern.</summary>
      /// <value>The path which has wildcard characters, for example, an asterisk (<see cref="Path.WildcardStarMatchAll"/>) or a question mark (<see cref="Path.WildcardQuestion"/>).</value>
      [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly")]
      public string SearchPattern
      {
         get { return _searchPattern; }

         internal set
         {
            if (Utils.IsNullOrWhiteSpace(value))
               throw new ArgumentNullException("SearchPattern");

            _searchPattern = value;

            _nameFilter = _searchPattern == Path.WildcardStarMatchAll
               ? null
               : new Regex("^" + Regex.Escape(_searchPattern).Replace(@"\.", ".").Replace(@"\*", ".*").Replace(@"\?", ".?") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
         }
      }

      #endregion // SearchPattern

      #region SkipReparsePoints

      /// <summary><see langword="true"/> skips ReparsePoints, <see langword="false"/> will follow ReparsePoints.</summary>
      public bool SkipReparsePoints { get; internal set; }

      #endregion // SkipReparsePoints

      #region Transaction

      /// <summary>Get or sets the KernelTransaction instance.</summary>
      /// <value>The transaction.</value>
      public KernelTransaction Transaction { get; internal set; }

      #endregion // Transaction

      #endregion // Properties
   }
}
