﻿using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Bose.Wearable.Proxy
{
	public class WearableProxyServer : MonoBehaviour
	{
		public int PortNumber
		{
			get { return _port; }
		}

		public bool ServerRunning
		{
			get { return _running; }
		}

		public int ConnectedClients
		{
			get { return (_running && _clientSlot != null && _clientSlot.Connected) ? 1 : 0; }
		}

		[SerializeField]
		private int _port;

		[SerializeField]
		private float _networkTimeout;

		private WearableProxyServerProtocol _protocol;
		private TcpListener _listener;
		private TcpClient _clientSlot;
		private byte[] _receiveBuffer;
		private int _receiveIndex;
		private byte[] _transmitBuffer;
		private int _transmitIndex;
		private bool _running;
		private WearableControl _wearableControl;
		private WearableDeviceProvider _deviceProvider;
		private bool _issuedWarningLastPacket;

		public void StartServer()
		{
			if (_running)
			{
				StopServer();
			}

			_listener.Start();
			_running = true;
		}

		public void StopServer()
		{
			if (!_running)
			{
				return;
			}

			if (_clientSlot != null)
			{
				_clientSlot.Close();
				_clientSlot = null;
			}

			_listener.Stop();
			_running = false;

			if (_deviceProvider.ConnectedDevice != null)
			{
				_deviceProvider.SetDeviceConfiguration(WearableConstants.DisabledDeviceConfig);
				_deviceProvider.DisconnectFromDevice();
			}
		}

		private void Awake()
		{
			_protocol = new WearableProxyServerProtocol();
			_protocol.KeepAlive += OnKeepAlivePacket;
			_protocol.SetNewConfig += OnSetConfigPacket;
			_protocol.ConnectToDevice += OnConnectToDevicePacket;
			_protocol.DisconnectFromDevice += OnDisconnectFromDevicePacket;
			_protocol.InitiateDeviceSearch += OnInitiateDeviceSearchPacket;
			_protocol.StopDeviceSearch += OnStopDeviceSearchPacket;
			_protocol.QueryConnectionStatus += OnQueryConnectionStatusPacket;
			_protocol.QueryConfigStatus += OnQueryConfigPacket;
			_protocol.RSSIFilterValueChange += OnRSSIFilterValueChangePacket;
			_protocol.PingQuery += OnPingQuery;

			_listener = new TcpListener(IPAddress.Any, _port);
			_receiveBuffer = new byte[WearableProxyProtocolBase.SuggestedClientToServerBufferSize];
			_receiveIndex = 0;
			_transmitBuffer = new byte[WearableProxyProtocolBase.SuggestedServerToClientBufferSize];
			_transmitIndex = 0;

			_networkTimeout = 0.5f;

			_running = false;

			_wearableControl = WearableControl.Instance;
			_wearableControl.ConnectionStatusChanged += OnConnectionStatusChanged;
			_deviceProvider = (WearableDeviceProvider)_wearableControl.GetOrCreateProvider<WearableDeviceProvider>();
		}

		private void OnDestroy()
		{
			_wearableControl.ConnectionStatusChanged -= OnConnectionStatusChanged;
		}

		private void Update()
		{
			if (!_running)
			{
				return;
			}

			// Accept new clients
			// TODO: Allow multiple clients
			if (_clientSlot == null && _listener.Pending())
			{
				_clientSlot = _listener.AcceptTcpClient();
				SendWelcomePackets();
			}

			// Scan for incoming data
			if (_clientSlot == null)
			{
				return;
			}

			try
			{
				NetworkStream stream = _clientSlot.GetStream();
				while (stream.DataAvailable)
				{
					int bufferSpaceRemaining = _receiveBuffer.Length - _receiveIndex;
					if (bufferSpaceRemaining <= 0)
					{
						// Can't fit any more packets or consume any more of the buffer; dump buffer to free space.
						Debug.LogWarning(WearableConstants.ProxyProviderBufferFullWarning);
						_receiveIndex = 0;
						bufferSpaceRemaining = _receiveBuffer.Length;
					}

					int actualBytesRead = stream.Read(_receiveBuffer, _receiveIndex, bufferSpaceRemaining);
					_receiveIndex += actualBytesRead;

					ProcessReceiveBuffer();
				}
			}
			catch (Exception)
			{
				// The client has disconnected, or some other error.
				_clientSlot.Close();
				_clientSlot = null;
				return;
			}

			if (_deviceProvider.CurrentSensorFrames.Count > 0)
			{
				_transmitIndex = 0;
				for (int i = 0; i < _deviceProvider.CurrentSensorFrames.Count; i++)
				{
					WearableProxyServerProtocol.EncodeSensorFrame(
						_transmitBuffer,
						ref _transmitIndex,
						_deviceProvider.CurrentSensorFrames[i]);
				}

				SendTransmitBuffer();
			}
		}

		private void SendWelcomePackets()
		{
			// Prepare to transmit
			_transmitIndex = 0;

			// Device connection info
			if (_deviceProvider.ConnectedDevice == null)
			{
				WearableProxyServerProtocol.EncodeConnectionStatus(
					_transmitBuffer,
					ref _transmitIndex,
					WearableProxyProtocolBase.ConnectionState.Disconnected,
					new Device {name = string.Empty, uid = WearableConstants.EmptyUID});
			}
			else
			{
				_transmitIndex = 0;
				WearableProxyServerProtocol.EncodeConnectionStatus(
					_transmitBuffer,
					ref _transmitIndex,
					WearableProxyProtocolBase.ConnectionState.Connected,
					_deviceProvider.ConnectedDevice.Value);
			}

			// Send config packet
			WearableProxyServerProtocol.EncodeConfigStatus(
				_transmitBuffer,
				ref _transmitIndex,
				_deviceProvider.GetCachedDeviceConfiguration());

			// Transmit
			SendTransmitBuffer();
		}

		private void ProcessReceiveBuffer()
		{
			// Process all packets in the buffer and delegate to relevant packet events
			int packetIndex = 0;
			while (packetIndex < _receiveIndex)
			{
				int packetStart = packetIndex;
				try
				{
					_protocol.ProcessPacket(_receiveBuffer, ref packetIndex);
				}
				catch (WearableProxyProtocolException exception)
				{
					// A packet could not be parsed, which means the whole buffer needs to be thrown away.
					if (!_issuedWarningLastPacket)
					{
						// Only issue warnings if we've previously parsed a packet correctly. This prevents flooding
						// in the case of mismatched versions, etc.
						Debug.LogWarning(exception.ToString());
						_issuedWarningLastPacket = true;
					}

					_receiveIndex = 0;
					return;
				}
				catch (IndexOutOfRangeException)
				{
					// The packet could not be completely decoded, meaning it is likely split across buffers.
					// Copy the fragment to the beginning of the buffer and try again the next time a buffer comes in.
					for (int i = packetStart; i < _receiveBuffer.Length; i++)
					{
						_receiveBuffer[i - packetStart] = _receiveBuffer[i];
					}

					// Position the receive index right after the partial packet
					_receiveIndex = _receiveBuffer.Length - packetStart;
					return;
				}

				_issuedWarningLastPacket = false;
			}

			_receiveIndex = 0;
		}

		private void SendTransmitBuffer()
		{
			if (_clientSlot == null || !_clientSlot.Connected)
			{
				// If we're not connected, we can't really do anything here. Show a warning and quit.
				Debug.LogWarning(WearableConstants.ProxyProviderNotConnectedWarning);
				return;
			}

			try
			{
				NetworkStream stream = _clientSlot.GetStream();
				stream.WriteTimeout = (int)(1000 * _networkTimeout);
				stream.Write(_transmitBuffer, 0, _transmitIndex);
			}
			catch (Exception)
			{
				_clientSlot.Close();
				_clientSlot = null;
			}
		}
		
		private void OnConnectionStatusChanged(ConnectionStatus status, Device? device)
		{
			switch (status)
			{
				case ConnectionStatus.Connecting:
					OnDeviceConnecting(device.GetValueOrDefault());
					break;
				case ConnectionStatus.Connected:
					OnDeviceConnected(device.GetValueOrDefault());
					break;
				case ConnectionStatus.Disconnected:
					OnDeviceDisconnected(device.GetValueOrDefault());
					break;
			}
		}

		private void OnDeviceConnecting(Device device)
		{
			_transmitIndex = 0;
			WearableProxyServerProtocol.EncodeConnectionStatus(
				_transmitBuffer,
				ref _transmitIndex,
				WearableProxyProtocolBase.ConnectionState.Connecting,
				device);

			SendTransmitBuffer();
		}

		private void OnDeviceConnected(Device device)
		{
			_transmitIndex = 0;
			WearableProxyServerProtocol.EncodeConnectionStatus(
				_transmitBuffer,
				ref _transmitIndex,
				WearableProxyProtocolBase.ConnectionState.Connected,
				device);

			SendTransmitBuffer();
		}

		private void OnDeviceDisconnected(Device device)
		{
			_transmitIndex = 0;
			WearableProxyServerProtocol.EncodeConnectionStatus(
				_transmitBuffer,
				ref _transmitIndex,
				WearableProxyProtocolBase.ConnectionState.Disconnected,
				device);

			SendTransmitBuffer();
		}

		private void OnDeviceConnectionFailed()
		{
			Device emptyDevice = new Device {uid = WearableConstants.EmptyUID, name = string.Empty, productId = ProductId.Undefined};
			_transmitIndex = 0;
			WearableProxyServerProtocol.EncodeConnectionStatus(
				_transmitBuffer,
				ref _transmitIndex,
				WearableProxyProtocolBase.ConnectionState.Failed,
				emptyDevice);

			SendTransmitBuffer();
		}

		private void OnDevicesUpdated(Device[] devices)
		{
			_transmitIndex = 0;
			WearableProxyServerProtocol.EncodeDeviceList(_transmitBuffer, ref _transmitIndex, devices);
			SendTransmitBuffer();
		}

		private void OnKeepAlivePacket()
		{
			// No-op
		}

		private void OnQueryConfigPacket()
		{
			_transmitIndex = 0;

			WearableProxyServerProtocol.EncodeConfigStatus(
				_transmitBuffer,
				ref _transmitIndex,
				_deviceProvider.GetCachedDeviceConfiguration());

			SendTransmitBuffer();
		}

		private void OnSetConfigPacket(WearableDeviceConfig config)
		{
			_deviceProvider.SetDeviceConfiguration(config);

			_transmitIndex = 0;

			WearableProxyServerProtocol.EncodeConfigStatus(
				_transmitBuffer,
				ref _transmitIndex,
				_deviceProvider.GetCachedDeviceConfiguration());

			SendTransmitBuffer();
		}

		private void OnConnectToDevicePacket(string uid)
		{
			Device device = new Device {uid = uid, name = string.Empty};
			_deviceProvider.ConnectToDevice(device, onSuccess: null, onFailure: OnDeviceConnectionFailed);
		}

		private void OnDisconnectFromDevicePacket()
		{
			if (_deviceProvider.ConnectedDevice == null)
			{
				return;
			}

			_deviceProvider.DisconnectFromDevice();
		}

		private void OnInitiateDeviceSearchPacket()
		{
			_deviceProvider.SearchForDevices(_wearableControl.GetActiveAppIntentProfile(), OnDevicesUpdated, false, 0f);
		}

		private void OnStopDeviceSearchPacket()
		{
			_deviceProvider.StopSearchingForDevices();
		}

		private void OnQueryConnectionStatusPacket()
		{
			_transmitIndex = 0;

			if (_deviceProvider.ConnectedDevice == null)
			{
				WearableProxyServerProtocol.EncodeConnectionStatus(
					_transmitBuffer,
					ref _transmitIndex,
					WearableProxyProtocolBase.ConnectionState.Disconnected,
					new Device());
			}
			else
			{
				_transmitIndex = 0;
				WearableProxyServerProtocol.EncodeConnectionStatus(
					_transmitBuffer,
					ref _transmitIndex,
					WearableProxyProtocolBase.ConnectionState.Connected,
					_deviceProvider.ConnectedDevice.Value);
			}

			SendTransmitBuffer();
		}

		private void OnPingQuery()
		{
			_transmitIndex = 0;
			WearableProxyProtocolBase.EncodePingResponse(_transmitBuffer, ref _transmitIndex);
			SendTransmitBuffer();
		}

		private void OnRSSIFilterValueChangePacket(int value)
		{
			_deviceProvider.SetRssiFilter(value);
		}
	}
}
