﻿using System;
using System.Collections.Generic;
using System.IO;
using Blamite.Blam;
using Blamite.Blam.Resources;
using Blamite.IO;
using Blamite.Util;
using Blamite.Blam.Localization;
using System.Linq;

namespace Blamite.Injection
{
	public class TagContainerInjector
	{
		private bool _keepSound;
		private bool _injectRaw;
		private bool _findExistingPages;
		private bool _renameShaders;
		private static int SoundClass = CharConstant.FromString("snd!");

		private static int PCAClass = CharConstant.FromString("pcaa");

		private readonly ICacheFile _cacheFile;
		private readonly TagContainer _container;

		private readonly Dictionary<DataBlock, uint> _dataBlockAddresses = new Dictionary<DataBlock, uint>();
		private readonly List<StringID> _injectedStrings = new List<StringID>();
		private readonly Dictionary<ResourcePage, int> _pageIndices = new Dictionary<ResourcePage, int>();

		private readonly Dictionary<ExtractedPage, int> _extractedResourcePages = new Dictionary<ExtractedPage, int>();

		private readonly Dictionary<ExtractedResourceInfo, DatumIndex> _resourceIndices =
			new Dictionary<ExtractedResourceInfo, DatumIndex>();

		private readonly Dictionary<ExtractedTag, DatumIndex> _tagIndices = new Dictionary<ExtractedTag, DatumIndex>();
		private CachedLanguagePackLoader _languageCache;
		private ResourceTable _resources;
		private IZoneSetTable _zoneSets;
		
		private static HashSet<string> _simulationClasses = new HashSet<string>(new string[] { "jpt!", "effe", "argd", "bipd", "bloc", "crea", "ctrl", "efsc", "eqip", "gint", "mach", "proj", "scen", "ssce", "term", "vehi", "weap" });

		private static HashSet<string> _shaderClasses = new HashSet<string>(new string[] { "mtsb", "mats", "pixl", "vtsh", "rmt2" });


		public TagContainerInjector(ICacheFile cacheFile, TagContainer container)
		{
			_cacheFile = cacheFile;
			_languageCache = new CachedLanguagePackLoader(cacheFile.Languages);
			_container = container;
			_keepSound = false;
			_injectRaw = false;
			_findExistingPages = false;
		}

		public TagContainerInjector(ICacheFile cacheFile, TagContainer container, bool keepsnd, bool injectraw, bool findexisting, bool renameshaders)
		{
			_cacheFile = cacheFile;
			_languageCache = new CachedLanguagePackLoader(cacheFile.Languages);
			_container = container;
			_keepSound = keepsnd;
			_injectRaw = injectraw;
			_findExistingPages = findexisting;
			_renameShaders = renameshaders;
		}

		public ICollection<DataBlock> InjectedBlocks
		{
			get { return _dataBlockAddresses.Keys; }
		}

		public ICollection<ExtractedTag> InjectedTags
		{
			get { return _tagIndices.Keys; }
		}

		public ICollection<ResourcePage> InjectedPages
		{
			get { return _pageIndices.Keys; }
		}

		public ICollection<ExtractedPage> InjectedExtractedResourcePages
		{
			get { return _extractedResourcePages.Keys; }
		}

		public ICollection<ExtractedResourceInfo> InjectedResources
		{
			get { return _resourceIndices.Keys; }
		}

		public ICollection<StringID> InjectedStringIDs
		{
			get { return _injectedStrings.AsReadOnly(); }
		}

		public void SaveChanges(IStream stream)
		{
			if (_resources != null)
			{
				_cacheFile.Resources.SaveResourceTable(_resources, stream);
				_resources = null;
			}
			if (_zoneSets != null && _zoneSets.GlobalZoneSet != null)
			{
				_zoneSets.SaveChanges(stream);
				_zoneSets = null;
			}
			_languageCache.SaveAll(stream);
			_languageCache.ClearCache();
			_cacheFile.SaveChanges(stream);
		}

		public DatumIndex InjectTag(ExtractedTag tag, IStream stream)
		{
			string tagnameuniqifier = "";

			if (tag == null)
				throw new ArgumentNullException("tag is null");

			// Don't inject the tag if it's already been injected
			DatumIndex newIndex;
			if (_tagIndices.TryGetValue(tag, out newIndex))
				return newIndex;

			// Make sure there isn't already a tag with the given name
			ITag existingTag = _cacheFile.Tags.FindTagByName(tag.Name, tag.Class, _cacheFile.FileNames);
			if (existingTag != null)
			{
				//check if we are doing shader tweaks
				if (_renameShaders && _shaderClasses.Contains(CharConstant.ToString(tag.Class)))
				{
					//append old tagid to make it unique
					tagnameuniqifier = "_" + tag.OriginalIndex.ToString();

					//make sure the appended name isn't already present
					existingTag = _cacheFile.Tags.FindTagByName(tag.Name + tagnameuniqifier, tag.Class, _cacheFile.FileNames);

					if (existingTag != null)
						return existingTag.Index;

				}	
				else
					return existingTag.Index;
			}
				
			if (!_keepSound && tag.Class == SoundClass)
				return DatumIndex.Null;
	
			//PCA resource type is not always present, so get rid of 'em for now
			if (tag.Class == PCAClass)
			return DatumIndex.Null;

			// Look up the tag's datablock to get its size and allocate a tag for it
			DataBlock tagData = _container.FindDataBlock(tag.OriginalAddress);
			if (_resources == null && BlockNeedsResources(tagData))
			{
				// If the tag relies on resources and that info isn't available, throw it out
				LoadResourceTable(stream);
				if (_resources == null)
					return DatumIndex.Null;
			}

			ITag newTag = _cacheFile.Tags.AddTag(tag.Class, tagData.Data.Length, stream);
			_tagIndices[tag] = newTag.Index;
			_cacheFile.FileNames.SetTagName(newTag, tag.Name + tagnameuniqifier);

			// Write the data
			WriteDataBlock(tagData, newTag.MetaLocation, stream, newTag);

			// Make the tag load
			LoadZoneSets(stream);
			if (_zoneSets != null && _zoneSets.GlobalZoneSet != null)
				_zoneSets.GlobalZoneSet.ActivateTag(newTag, true);

			// If its class matches one of the valid simulation class names, add it to the simulation definition table
			if (_cacheFile.SimulationDefinitions != null && _simulationClasses.Contains(CharConstant.ToString(newTag.Class.Magic)))
				_cacheFile.SimulationDefinitions.Add(newTag);

			if (CharConstant.ToString(newTag.Class.Magic) == "paas")
			{
				IPolyart vertex = new Blamite.Blam.ThirdGen.Structures.ThirdGenPolyart(newTag.MetaLocation.AsPointer() + 0x44, 6);
				IPolyart index = new Blamite.Blam.ThirdGen.Structures.ThirdGenPolyart(newTag.MetaLocation.AsPointer() + 0x50, 8);

				_cacheFile.PolyartTable.Add(vertex);
				_cacheFile.PolyartTable.Add(index);
				
			}
				

			return newTag.Index;
		}

		public DatumIndex InjectTag(DatumIndex originalIndex, IStream stream)
		{
			return InjectTag(_container.FindTag(originalIndex), stream);
		}

		public uint InjectDataBlock(DataBlock block, IStream stream)
		{
			if (block == null)
				throw new ArgumentNullException("block is null");

			// Don't inject the block if it's already been injected
			uint newAddress;
			if (_dataBlockAddresses.TryGetValue(block, out newAddress))
				return newAddress;

			// Allocate space for it and write it to the file
			newAddress = _cacheFile.Allocator.Allocate(block.Data.Length, (uint)block.Alignment, stream);
			SegmentPointer location = SegmentPointer.FromPointer(newAddress, _cacheFile.MetaArea);
			WriteDataBlock(block, location, stream);
			return newAddress;
		}

		public uint InjectDataBlock(uint originalAddress, IStream stream)
		{
			return InjectDataBlock(_container.FindDataBlock(originalAddress), stream);
		}

		public int InjectResourcePage(ResourcePage page, IStream stream)
		{
			if (_resources == null)
				return -1;
			if (page == null)
				throw new ArgumentNullException("page is null");

			// Don't inject the page if it's already been injected
			int newIndex;
			if (_pageIndices.TryGetValue(page, out newIndex))
				return newIndex;

			// Add the page and associate its new index with it
			var extractedRaw = _container.FindExtractedResourcePage(page.Index);
			newIndex = _resources.Pages.Count;
			page.Index = newIndex; // haxhaxhax, oh aaron
			LoadResourceTable(stream);

			// Inject?
			if (_injectRaw && extractedRaw != null)
			{
				if (_findExistingPages && page.FilePath != null &&
					(page.FilePath.Contains("mainmenu") || page.FilePath.Contains("shared") ||
					((page.FilePath.Contains("campaign") && (_cacheFile.Type == CacheFileType.SinglePlayer)))))
				{
					// Nothing!
				}
				else
				{
					var rawOffset = InjectExtractedResourcePage(page, extractedRaw, stream);
					page.Offset = rawOffset;
					page.FilePath = null;
				}

				
			}

			_resources.Pages.Add(page);
			_pageIndices[page] = newIndex;
			return newIndex;
		}

		public int InjectResourcePage(int originalIndex, IStream stream)
		{
			return InjectResourcePage(_container.FindResourcePage(originalIndex), stream);
		}

		public int InjectExtractedResourcePage(ResourcePage resourcePage, ExtractedPage extractedPage, IStream stream)
		{
			if (extractedPage == null)
				throw new ArgumentNullException("extractedPage");

			var injector = new ResourcePageInjector(_cacheFile);
			var rawOffset = injector.InjectPage(stream, resourcePage, extractedPage.ExtractedPageData);
			
			_extractedResourcePages[extractedPage] = extractedPage.ResourcePageIndex;

			return rawOffset;
		}

		public DatumIndex InjectResource(ExtractedResourceInfo resource, IStream stream)
		{
			if (_resources == null)
				return DatumIndex.Null;
			if (resource == null)
				throw new ArgumentNullException("resource is null");

			// Don't inject the resource if it's already been injected
			DatumIndex newIndex;
			if (_resourceIndices.TryGetValue(resource, out newIndex))
				return newIndex;

			// Create a new datum index for it (0x4152 = 'AR') and associate it
			LoadResourceTable(stream);
			newIndex = new DatumIndex(0x4152, (ushort) _resources.Resources.Count);
			_resourceIndices[resource] = newIndex;

			// Create a native resource for it
			var newResource = new Resource();
			_resources.Resources.Add(newResource);
			newResource.Index = newIndex;
			newResource.Flags = resource.Flags;
			newResource.Type = resource.Type;
			newResource.Info = resource.Info;

			if (resource.OriginalParentTagIndex.IsValid)
			{
				DatumIndex parentTagIndex = InjectTag(resource.OriginalParentTagIndex, stream);
				newResource.ParentTag = _cacheFile.Tags[parentTagIndex];
			}
			if (resource.Location != null)
			{
				newResource.Location = new ResourcePointer();

				// Primary page pointers
				if (resource.Location.OriginalPrimaryPageIndex >= 0)
				{
					int primaryPageIndex = -1;

					if (_findExistingPages) //find existing entry to point to
						primaryPageIndex = _resources.Pages.FindIndex(r => r.Checksum == _container.FindResourcePage(resource.Location.OriginalPrimaryPageIndex).Checksum);
 
					if (primaryPageIndex == -1)
						primaryPageIndex = InjectResourcePage(resource.Location.OriginalPrimaryPageIndex, stream);

					newResource.Location.PrimaryPage = _resources.Pages[primaryPageIndex];
				}
				newResource.Location.PrimaryOffset = resource.Location.PrimaryOffset;
				newResource.Location.PrimarySize = resource.Location.PrimarySize;

				// Secondary page pointers
				if (resource.Location.OriginalSecondaryPageIndex >= 0)
				{
					int secondaryPageIndex = -1;

					if (_findExistingPages) //find existing entry to point to
						secondaryPageIndex = _resources.Pages.FindIndex(r => r.Checksum == _container.FindResourcePage(resource.Location.OriginalSecondaryPageIndex).Checksum);

					if (secondaryPageIndex == -1)
						secondaryPageIndex = InjectResourcePage(resource.Location.OriginalSecondaryPageIndex, stream);

					newResource.Location.SecondaryPage = _resources.Pages[secondaryPageIndex];
				}
				newResource.Location.SecondaryOffset = resource.Location.SecondaryOffset;
				newResource.Location.SecondarySize = resource.Location.SecondarySize;


				// tert page pointers
				if (resource.Location.OriginalTertiaryPageIndex >= 0)
				{
					int tertiaryPageIndex = -1;

					if (_findExistingPages) //find existing entry to point to
						tertiaryPageIndex = _resources.Pages.FindIndex(r => r.Checksum == _container.FindResourcePage(resource.Location.OriginalTertiaryPageIndex).Checksum);

					if (tertiaryPageIndex == -1)
						tertiaryPageIndex = InjectResourcePage(resource.Location.OriginalTertiaryPageIndex, stream);

					newResource.Location.TertiaryPage = _resources.Pages[tertiaryPageIndex];
				}
				newResource.Location.TertiaryOffset = resource.Location.TertiaryOffset;
				newResource.Location.TertiarySize = resource.Location.TertiarySize;
			}

			newResource.ResourceFixups.AddRange(resource.ResourceFixups);
			newResource.DefinitionFixups.AddRange(resource.DefinitionFixups);

			newResource.ResourceBits = resource.ResourceBits;
			newResource.BaseDefinitionAddress = resource.BaseDefinitionAddress;

			// Make it load
			LoadZoneSets(stream);
			if (_zoneSets != null && _zoneSets.GlobalZoneSet != null)
				_zoneSets.GlobalZoneSet.ActivateResource(newResource, true);

			return newIndex;
		}

		public DatumIndex InjectResource(DatumIndex originalIndex, IStream stream)
		{
			return InjectResource(_container.FindResource(originalIndex), stream);
		}

		private bool BlockNeedsResources(DataBlock block)
		{
			if (block.ResourceFixups.Count > 0)
				return true;

			foreach (var addrFixup in block.AddressFixups)
			{
				var subBlock = _container.FindDataBlock(addrFixup.OriginalAddress);
				if (BlockNeedsResources(subBlock))
					return true;
			}
			return false;
		}

		private void WriteDataBlock(DataBlock block, SegmentPointer location, IStream stream, ITag tag = null)
		{
			if (tag == null && _dataBlockAddresses.ContainsKey(block)) // Don't write anything if the block has already been written
				return;

			// Associate the location with the block
			_dataBlockAddresses[block] = location.AsPointer();

			// Create a MemoryStream and write the block data to it (so fixups can be performed before writing it to the file)
			using (var buffer = new MemoryStream(block.Data.Length))
			{
				var bufferWriter = new EndianWriter(buffer, stream.Endianness);
				bufferWriter.WriteBlock(block.Data);

				// Apply fixups
				FixBlockReferences(block, bufferWriter, stream);
				FixTagReferences(block, bufferWriter, stream);
				FixResourceReferences(block, bufferWriter, stream);
				FixStringIdReferences(block, bufferWriter);
				if (tag != null)
					FixUnicListReferences(block, tag, bufferWriter, stream);

				// sort after fixups
				if (block.Sortable && block.EntrySize >= 4)
				{
					var entries = new List<Tuple<uint, byte[]>>();
					var bufferReader = new EndianReader(buffer, stream.Endianness);

					for (int i = 0; i < block.EntryCount; i++)
					{
						buffer.Position = i * block.EntrySize;
						uint sid = bufferReader.ReadUInt32();
						byte[] rest = bufferReader.ReadBlock(block.EntrySize - 4);
						entries.Add(new Tuple<uint, byte[]>(sid, rest));
					}
					buffer.Position = 0;
					foreach (var entry in entries.OrderBy(e => e.Item1))
					{
						bufferWriter.WriteUInt32(entry.Item1);
						bufferWriter.WriteBlock(entry.Item2);
					}
				}

				// Write the buffer to the file
				stream.SeekTo(location.AsOffset());
				stream.WriteBlock(buffer.ToArray(), 0, (int) buffer.Length);
			}

			// Write shader fixups (they can't be done in-memory because they require cache file expansion)
			FixShaderReferences(block, stream, location);
		}

		private void FixBlockReferences(DataBlock block, IWriter buffer, IStream stream)
		{
			foreach (DataBlockAddressFixup fixup in block.AddressFixups)
			{
				uint newAddress = InjectDataBlock(fixup.OriginalAddress, stream);
				buffer.SeekTo(fixup.WriteOffset);
				buffer.WriteUInt32(newAddress);
			}
		}

		private void FixTagReferences(DataBlock block, IWriter buffer, IStream stream)
		{
			foreach (DataBlockTagFixup fixup in block.TagFixups)
			{
				DatumIndex newIndex = InjectTag(fixup.OriginalIndex, stream);
				buffer.SeekTo(fixup.WriteOffset);
				buffer.WriteUInt32(newIndex.Value);
			}
		}

		private void FixResourceReferences(DataBlock block, IWriter buffer, IStream stream)
		{
			foreach (DataBlockResourceFixup fixup in block.ResourceFixups)
			{
				DatumIndex newIndex = InjectResource(fixup.OriginalIndex, stream);
				buffer.SeekTo(fixup.WriteOffset);
				buffer.WriteUInt32(newIndex.Value);
			}
		}

		private void FixStringIdReferences(DataBlock block, IWriter buffer)
		{
			foreach (DataBlockStringIDFixup fixup in block.StringIDFixups)
			{
				StringID newSID = InjectStringID(fixup.OriginalString);
				buffer.SeekTo(fixup.WriteOffset);
				buffer.WriteUInt32(newSID.Value);
			}
		}

		private void FixShaderReferences(DataBlock block, IStream stream, SegmentPointer baseOffset)
		{
			foreach (DataBlockShaderFixup fixup in block.ShaderFixups)
			{
				stream.SeekTo(baseOffset.AsOffset() + fixup.WriteOffset);
				_cacheFile.ShaderStreamer.ImportShader(fixup.Data, stream);
			}
		}

		private void FixUnicListReferences(DataBlock block, ITag tag, IWriter buffer, IStream stream)
		{
			foreach (DataBlockUnicListFixup fixup in block.UnicListFixups)
			{
				var pack = _languageCache.LoadLanguage((GameLanguage)fixup.LanguageIndex, stream);
				var stringList = new LocalizedStringList(tag);
				foreach (var str in fixup.Strings)
				{
					var id = InjectStringID(str.StringID);
					stringList.Strings.Add(new LocalizedString(id, str.String));
				}
				pack.AddStringList(stringList);
			}
		}

		private StringID InjectStringID(string str)
		{
			// Try to find the string, and if it's not found, inject it
			StringID newSID = _cacheFile.StringIDs.FindStringID(str);
			if (newSID == StringID.Null)
			{
				newSID = _cacheFile.StringIDs.AddString(str);
				_injectedStrings.Add(newSID);
			}
			return newSID;
		}

		private void LoadResourceTable(IReader reader)
		{
			if (_resources == null)
				_resources = _cacheFile.Resources.LoadResourceTable(reader);
		}

		private void LoadZoneSets(IReader reader)
		{
			if (_zoneSets == null)
				_zoneSets = _cacheFile.Resources.LoadZoneSets(reader);
		}
	}
}
