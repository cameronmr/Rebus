﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Rebus.DataBus
{
    class DefaultDataBus : IDataBus
    {
        readonly IDataBusStorage _dataBusStorage;

        public DefaultDataBus(IDataBusStorage dataBusStorage)
        {
            if (dataBusStorage == null) throw new ArgumentNullException(nameof(dataBusStorage));
            _dataBusStorage = dataBusStorage;
        }

        public async Task<DataBusAttachment> CreateAttachment(Stream source, Dictionary<string, string> optionalMetadata = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var id = Guid.NewGuid().ToString();

            await _dataBusStorage.Save(id, source, optionalMetadata);

            var attachment = new DataBusAttachment(id);

            return attachment;
        }

        public async Task<Stream> OpenRead(string dataBusAttachmentId)
        {
            return await _dataBusStorage.Read(dataBusAttachmentId);
        }

        public async Task<Dictionary<string, string>> GetMetadata(string dataBusAttachmentId)
        {
            return await _dataBusStorage.ReadMetadata(dataBusAttachmentId);
        }
    }
}