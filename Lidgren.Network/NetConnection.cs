﻿/* Copyright (c) 2010 Michael Lidgren

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.

*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;

namespace Lidgren.Network
{
	[DebuggerDisplay("RemoteEndpoint={m_remoteEndpoint} Status={m_status}")]
	public partial class NetConnection
	{
		private static readonly NetFragmentationInfo s_genericFragmentationInfo = new NetFragmentationInfo();

		internal readonly NetPeer m_owner;
		internal readonly IPEndPoint m_remoteEndpoint;
		internal double m_lastHeardFrom;
		internal readonly NetQueue<NetSending> m_unsentMessages;
		internal NetConnectionStatus m_status;
		internal NetConnectionStatus m_visibleStatus;
		private double m_lastSentUnsentMessages;
		private float m_throttleDebt;
		private readonly NetPeerConfiguration m_peerConfiguration;
		internal NetConnectionStatistics m_statistics;
		private int m_lesserHeartbeats;
		private int m_nextFragmentGroupId;
		internal long m_remoteUniqueIdentifier;
		private readonly Dictionary<int, NetIncomingMessage> m_fragmentGroups;
		private int m_handshakeAttempts;

		internal PendingConnectionStatus m_pendingStatus = PendingConnectionStatus.NotPending;
		internal string m_pendingDenialReason;

		/// <summary>
		/// Gets or sets the object containing data about the connection
		/// </summary>
		public object Tag { get; set; }

		/// <summary>
		/// Statistics for the connection
		/// </summary>
		public NetConnectionStatistics Statistics { get { return m_statistics; } }

		/// <summary>
		/// The unique identifier of the remote NetPeer for this connection
		/// </summary>
		public long RemoteUniqueIdentifier { get { return m_remoteUniqueIdentifier; } }

		/// <summary>
		/// The current status of the connection
		/// </summary>
		public NetConnectionStatus Status
		{
			get { return m_visibleStatus; }
		}

		/// <summary>
		/// Gets the remote endpoint for the connection
		/// </summary>
		public IPEndPoint RemoteEndpoint { get { return m_remoteEndpoint; } }

		/// <summary>
		/// Gets the owning NetPeer instance
		/// </summary>
		public NetPeer Owner { get { return m_owner; } }

		/// <summary>
		/// Gets the number of bytes queued for sending to this connection
		/// </summary>
		public int UnsentBytesCount
		{
			get
			{
				int mtu = m_owner.Configuration.MaximumTransmissionUnit - NetConstants.FragmentHeaderSize;
				int retval = 0;

				NetSending[] arr = m_unsentMessages.ToArray();
				foreach (NetSending send in arr)
				{
					if (send.FragmentGroupId == 0)
					{
						retval += send.Message.LengthBytes;
					}
					else
					{
						int thisFragmentLength = (send.FragmentNumber == send.FragmentTotalCount - 1 ? (send.Message.LengthBytes - (mtu * (send.FragmentTotalCount - 1))) : mtu);
						retval += thisFragmentLength;
					}
				}
				return retval;
			}
		}

		internal NetConnection(NetPeer owner, IPEndPoint remoteEndpoint)
		{
			m_owner = owner;
			m_peerConfiguration = m_owner.m_configuration;
			m_remoteEndpoint = remoteEndpoint;
			m_fragmentGroups = new Dictionary<int, NetIncomingMessage>();
			m_status = NetConnectionStatus.None;
			m_visibleStatus = NetConnectionStatus.None;
			m_unsentMessages = new NetQueue<NetSending>(8);

			double now = NetTime.Now;
			m_nextPing = now + 10.0f;
			m_nextForceAckTime = double.MaxValue;
			m_lastSentUnsentMessages = now;
			m_lastSendRespondedTo = now;
			m_statistics = new NetConnectionStatistics(this);

			//InitializeReliability();
			int num = ((int)NetMessageType.UserReliableOrdered + NetConstants.NetChannelsPerDeliveryMethod) - (int)NetMessageType.UserSequenced;
			m_nextSendSequenceNumber = new int[num];
			m_lastReceivedSequenced = new ushort[num];
			for (int i = 0; i < m_lastReceivedSequenced.Length; i++)
				m_lastReceivedSequenced[i] = ushort.MaxValue;
			m_nextForceAckTime = double.MaxValue;
		}

		// run on network thread
		internal void Heartbeat(double now)
		{
			m_owner.VerifyNetworkThread();

			m_lesserHeartbeats++;

			if (m_lesserHeartbeats >= 2)
			{
				//
				// Do greater heartbeat every third heartbeat
				//
				m_lesserHeartbeats = 0;

				// keepalive, timeout and ping stuff
				KeepAliveHeartbeat(now);

				if (m_connectRequested)
					SendConnect();

				if (m_status == NetConnectionStatus.Connecting && now - m_connectInitationTime > m_owner.m_configuration.m_handshakeAttemptDelay)
				{
					if (m_connectionInitiator)
						SendConnect();
					else
						SendConnectResponse();

					m_connectInitationTime = now;

					if (++m_handshakeAttempts >= m_owner.m_configuration.m_handshakeMaxAttempts)
					{
						Disconnect("Failed to complete handshake");
						return;
					}
				}

				// queue resends
				foreach (NetSending send in m_unackedSends)
				{
					if (now > send.NextResend)
					{
						m_owner.LogVerbose("Resending " + send);
						m_unsentMessages.EnqueueFirst(send);
						send.SetNextResend(this);
					}
				}

				/*
				if (!m_storedMessagesNotEmpty.IsEmpty())
				{
					int first = m_storedMessagesNotEmpty.GetFirstSetIndex();

#if DEBUG
					// slow slow verification
					for (int i = 0; i < first; i++)
						if (m_storedMessages[i] != null && m_storedMessages[i].Count > 0)
							throw new NetException("m_storedMessagesNotEmpty mismatch; first is " + first + " but actual first is " + i);
					if (m_storedMessages[first] == null || m_storedMessages[first].Count < 1)
						throw new NetException("m_storedMessagesNotEmpty failure; first is " + first + ", but that entry is empty!");
#endif
					for (int i = first; i < m_storedMessages.Length; i++)
					{
						if (m_storedMessagesNotEmpty.Get(i))
						{
							Dictionary<ushort, NetOutgoingMessage> dict = m_storedMessages[i];
						RestartCheck:
							foreach (ushort seqNr in m_storedMessages[i].Keys)
							{
								NetOutgoingMessage om = dict[seqNr];
								if (now >= om.m_nextResendTime)
								{
									Resend(now, seqNr, om);
									goto RestartCheck; // need to break out here; collection may have been modified
								}
							}
						}
#if DEBUG
						else
						{
							NetException.Assert(m_storedMessages[i] == null || m_storedMessages[i].Count < 1, "m_storedMessagesNotEmpty fail!");
						}
#endif
					}
				}
				*/
			}

			// send unsent messages; high priority first
			byte[] buffer = m_owner.m_sendBuffer;
			int ptr = 0;

			float throttle = m_peerConfiguration.m_throttleBytesPerSecond;
			if (throttle > 0)
			{
				double frameLength = now - m_lastSentUnsentMessages;
				if (m_throttleDebt > 0)
					m_throttleDebt -= (float)(frameLength * throttle);
			}
			else
			{
				// 0 = unlimited (but still respect throttlethreshold per iteration)
				m_throttleDebt = 0;
			}

			m_lastSentUnsentMessages = now;

			int mtu = m_peerConfiguration.m_maximumTransmissionUnit;
			bool useCoalescing = m_peerConfiguration.m_useMessageCoalescing;

			float throttleThreshold = m_peerConfiguration.m_throttlePeakBytes;
			if (m_throttleDebt < throttleThreshold)
			{
				//
				// Send new unsent messages
				//
				int numIncludedMessages = 0;
				while (m_unsentMessages.Count > 0)
				{
					if (m_throttleDebt >= throttleThreshold)
						break;

					NetSending send;
					if (!m_unsentMessages.TryDequeue(out send))
						continue;

					send.NumSends++;

					NetOutgoingMessage msg = send.Message;
					int msgPayloadLength = msg.LengthBytes;

					if (ptr > 0)
					{
						if (!useCoalescing || ((ptr + NetPeer.kMaxPacketHeaderSize + msgPayloadLength) > mtu))
						{
							// send packet and start new packet
							bool connectionReset;
							m_owner.SendPacket(ptr, m_remoteEndpoint, numIncludedMessages, out connectionReset);
							if (connectionReset)
							{
								// ouch! can't sent any more; lets disconnect
								Disconnect(NetConstants.ConnResetMessage);
								ptr = 0;
								numIncludedMessages = 0;
								break;
							}
							m_statistics.PacketSent(ptr, numIncludedMessages);
							numIncludedMessages = 0;
							m_throttleDebt += ptr;
							ptr = 0;
						}
					}

					//
					// encode message
					//

					if (send.FragmentGroupId > 0)
						ptr = msg.EncodeFragmented(buffer, ptr, send, mtu);
					else
						ptr = msg.EncodeUnfragmented(buffer, ptr, send.MessageType, send.SequenceNumber);
					numIncludedMessages++;

					if (send.MessageType >= NetMessageType.UserReliableUnordered)
					{
						// store for reliability
						if (send.NumSends == 1)
							m_unackedSends.Add(send);
					}
					else
					{
						// unreliable message; recycle if all sendings done
						int unfin = msg.m_numUnfinishedSendings;
						msg.m_numUnfinishedSendings = unfin - 1;
						if (unfin <= 1)
							m_owner.Recycle(msg);
					}

					// room to piggyback some acks?
					if (m_acknowledgesToSend.Count > 0)
					{
						int payloadLeft = (mtu - ptr) - NetPeer.kMaxPacketHeaderSize;
						if (payloadLeft > 9)
						{
							// yes, add them as a regular message
							ptr = NetOutgoingMessage.EncodeAcksMessage(m_owner.m_sendBuffer, ptr, this, (payloadLeft - 3));

							if (m_acknowledgesToSend.Count < 1)
								m_nextForceAckTime = double.MaxValue;
						}
					}

					// when sending disconnect we can finish our own disconnect
					if (send.MessageType == NetMessageType.Library && msg.m_libType == NetMessageLibraryType.Disconnect)
					{
						FinishDisconnect();
						break;
					}
				}

				if (ptr > 0)
				{
					bool connectionReset;
					m_owner.SendPacket(ptr, m_remoteEndpoint, numIncludedMessages, out connectionReset);
					if (connectionReset)
					{
						// ouch! can't sent any more; lets disconnect
						Disconnect(NetConstants.ConnResetMessage);
					}
					else
					{
						m_statistics.PacketSent(ptr, numIncludedMessages);
						numIncludedMessages = 0;
						m_throttleDebt += ptr;
					}
				}
			}
		}

		internal void HandleUserMessage(double now, NetMessageType mtp, bool isFragment, ushort channelSequenceNumber, int ptr, int payloadLengthBits)
		{
			m_owner.VerifyNetworkThread();

			m_owner.LogVerbose("Received over wire: " + mtp + "#" + channelSequenceNumber);
			try
			{
				NetDeliveryMethod ndm = NetPeer.GetDeliveryMethod(mtp);

				//
				// Unreliable
				//
				if (ndm == NetDeliveryMethod.Unreliable)
				{
					AcceptMessage(mtp, isFragment, channelSequenceNumber, ptr, payloadLengthBits);
					return;
				}

				//
				// UnreliableSequenced
				//
				if (ndm == NetDeliveryMethod.UnreliableSequenced)
				{
					bool reject = ReceivedSequencedMessage(mtp, channelSequenceNumber);
					if (!reject)
						AcceptMessage(mtp, isFragment, channelSequenceNumber, ptr, payloadLengthBits);
					return;
				}

				//
				// Reliable delivery methods below
				//

				// queue ack; regardless if this is a duplicate or not
				m_acknowledgesToSend.Enqueue((int)channelSequenceNumber | ((int)mtp << 16));
				if (m_nextForceAckTime == double.MaxValue)
					m_nextForceAckTime = now + m_peerConfiguration.m_maxAckDelayTime;

				if (ndm == NetDeliveryMethod.ReliableSequenced)
				{
					bool reject = ReceivedSequencedMessage(mtp, channelSequenceNumber);
					if (!reject)
						AcceptMessage(mtp, isFragment, channelSequenceNumber, ptr, payloadLengthBits);
					return;
				}

				// relate to all received up to
				int reliableSlot = (int)mtp - (int)NetMessageType.UserReliableUnordered;
				int diff = Relate(channelSequenceNumber, m_nextExpectedReliableSequence[reliableSlot]);

				if (diff > (ushort.MaxValue / 2))
				{
					// Reject out-of-window
					//m_statistics.CountDuplicateMessage(msg);
					m_owner.LogVerbose("Rejecting duplicate reliable " + mtp + " " + channelSequenceNumber);
					return;
				}

				if (diff == 0)
				{
					// Expected sequence number
					AcceptMessage(mtp, isFragment, channelSequenceNumber, ptr, payloadLengthBits);
					
					ExpectedReliableSequenceArrived(reliableSlot, isFragment);
					return;
				}

				//
				// Early reliable message - we must check if it's already been received
				//
				// DeliveryMethod is ReliableUnordered or ReliableOrdered here
				//

				// get bools list we must check
				NetBitVector recList = m_reliableReceived[reliableSlot];
				if (recList == null)
				{
					recList = new NetBitVector(NetConstants.NumSequenceNumbers);
					m_reliableReceived[reliableSlot] = recList;
				}

				if (recList[channelSequenceNumber])
				{
					// Reject duplicate
					//m_statistics.CountDuplicateMessage(msg);
					m_owner.LogVerbose("Rejecting duplicate reliable " + ndm.ToString() + channelSequenceNumber.ToString());
					return;
				}

				// It's an early reliable message
				m_owner.LogVerbose("Received early reliable message: " + channelSequenceNumber);

				//
				// It's not a duplicate; mark as received. Release if it's unordered, else withhold
				//
				recList[channelSequenceNumber] = true;

				if (ndm == NetDeliveryMethod.ReliableUnordered)
				{
					AcceptMessage(mtp, isFragment, channelSequenceNumber, ptr, payloadLengthBits);
					return;
				}

				//
				// Only ReliableOrdered left here; withhold it
				//

				// Early ordered message; withhold
				const int orderedSlotsStart = ((int)NetMessageType.UserReliableOrdered - (int)NetMessageType.UserReliableUnordered);
				int orderedSlot = reliableSlot - orderedSlotsStart;

				List<NetIncomingMessage> wmList = m_withheldMessages[orderedSlot];
				if (wmList == null)
				{
					wmList = new List<NetIncomingMessage>();
					m_withheldMessages[orderedSlot] = wmList;
				}

				// create message
				NetIncomingMessage im = m_owner.CreateIncomingMessage(NetIncomingMessageType.Data, m_owner.m_receiveBuffer, ptr, NetUtility.BytesToHoldBits(payloadLengthBits));
				im.m_bitLength = payloadLengthBits;
				im.m_messageType = mtp;
				im.m_sequenceNumber = channelSequenceNumber;
				im.m_senderConnection = this;
				im.m_senderEndpoint = m_remoteEndpoint;
				if (isFragment)
					im.m_fragmentationInfo = s_genericFragmentationInfo;

				m_owner.LogVerbose("Withholding " + im + " (waiting for " + m_nextExpectedReliableSequence[reliableSlot] + ")");

				wmList.Add(im);

				return;
			}
			catch (Exception ex)
			{
#if DEBUG
				throw new NetException("Message generated exception: " + ex, ex);
#else
				m_owner.LogError("Message generated exception: " + ex);
				return;
#endif
			}
		}

		private void AcceptMessage(NetMessageType mtp, bool isFragment, ushort seqNr, int ptr, int payloadLengthBits)
		{
			byte[] buffer = m_owner.m_receiveBuffer;
			NetIncomingMessage im;
			int bytesLen = NetUtility.BytesToHoldBits(payloadLengthBits);

			if (isFragment)
			{
				int fragmentGroup = buffer[ptr++] | (buffer[ptr++] << 8);
				int fragmentTotalCount = buffer[ptr++] | (buffer[ptr++] << 8);
				int fragmentNr = buffer[ptr++] | (buffer[ptr++] << 8);

				// do we already have fragments of this group?
				if (!m_fragmentGroups.TryGetValue(fragmentGroup, out im))
				{
					// new fragmented message
					int estLength = fragmentTotalCount * bytesLen;

					im = m_owner.CreateIncomingMessage(NetIncomingMessageType.Data, estLength);
					im.m_messageType = mtp;
					im.m_sequenceNumber = seqNr;
					im.m_senderConnection = this;
					im.m_senderEndpoint = m_remoteEndpoint;

					NetFragmentationInfo info = new NetFragmentationInfo();
					info.TotalFragmentCount = fragmentTotalCount;
					info.Received = new bool[fragmentTotalCount];
					info.FragmentSize = bytesLen;
					im.m_fragmentationInfo = info;

					m_fragmentGroups[fragmentGroup] = im;
				}

				// insert this fragment at correct position
				bool done = InsertFragment(im, fragmentNr, ptr, bytesLen);
				if (!done)
					return;

				// all received!
				im.m_fragmentationInfo = null;
				m_fragmentGroups.Remove(fragmentGroup);
			}
			else
			{
				// non-fragmented - release to application
				im = m_owner.CreateIncomingMessage(NetIncomingMessageType.Data, buffer, ptr, bytesLen);
				im.m_bitLength = payloadLengthBits;
				im.m_messageType = mtp;
				im.m_sequenceNumber = seqNr;
				im.m_senderConnection = this;
				im.m_senderEndpoint = m_remoteEndpoint;
			}

			m_owner.LogVerbose("Releasing " + im);
			m_owner.ReleaseMessage(im);
		}

		private bool InsertFragment(NetIncomingMessage im, int nr, int ptr, int payloadLength)
		{
			NetFragmentationInfo info = im.m_fragmentationInfo;

			if (nr >= info.TotalFragmentCount)
			{
				m_owner.LogError("Received fragment larger than total fragments! (total " + info.TotalFragmentCount + ", nr " + nr + ")");
				return false;
			}

			if (info.Received[nr] == true)
			{
				// duplicate fragment
				return false;
			}

			// insert data
			int offset = nr * info.FragmentSize;

			if (im.m_data.Length < offset + payloadLength)
			{
				byte[] arr = im.m_data;
				Array.Resize<byte>(ref arr, offset + payloadLength);
			}

			Buffer.BlockCopy(m_owner.m_receiveBuffer, ptr, im.m_data, offset, payloadLength);

			// only enlarge message length if this is latest fragment received
			int newBitLength = (8 * (offset + payloadLength));
			if (newBitLength > im.m_bitLength)
				im.m_bitLength = newBitLength;

			info.Received[nr] = true;
			info.TotalReceived++;

			m_owner.LogVerbose("Got fragment " + nr + "/" + info.TotalFragmentCount + " (num received: " + info.TotalReceived + ")");

			return info.TotalReceived >= info.TotalFragmentCount;
		}

		internal void HandleLibraryMessage(double now, NetMessageLibraryType libType, int ptr, int payloadLengthBits)
		{
			m_owner.VerifyNetworkThread();

			switch (libType)
			{
				case NetMessageLibraryType.Error:
					m_owner.LogWarning("Received NetMessageLibraryType.Error message!");
					break;
				case NetMessageLibraryType.Connect:
				case NetMessageLibraryType.ConnectResponse:
				case NetMessageLibraryType.ConnectionEstablished:
				case NetMessageLibraryType.Disconnect:
					HandleIncomingHandshake(libType, ptr, payloadLengthBits);
					break;
				case NetMessageLibraryType.KeepAlive:
					// no operation, we just want the acks
					break;
				case NetMessageLibraryType.Ping:
					if (NetUtility.BytesToHoldBits(payloadLengthBits) > 0)
						HandleIncomingPing(m_owner.m_receiveBuffer[ptr]);
					else
						m_owner.LogWarning("Received malformed ping");
					break;
				case NetMessageLibraryType.Pong:
					if (payloadLengthBits == (9 * 8))
					{
						byte pingNr = m_owner.m_receiveBuffer[ptr++];
						double remoteNetTime = BitConverter.ToDouble(m_owner.m_receiveBuffer, ptr);
						HandleIncomingPong(now, pingNr, remoteNetTime);
					}
					else
					{
						m_owner.LogWarning("Received malformed pong");
					}
					break;
				case NetMessageLibraryType.Acknowledge:
					HandleIncomingAcks(ptr, NetUtility.BytesToHoldBits(payloadLengthBits));
					break;
				default:
					m_owner.LogWarning("Unhandled library type in " + this + ": " + libType);
					break;
			}

			return;
		}

		internal void SendLibrary(NetOutgoingMessage msg)
		{
			NetException.Assert(msg.m_libType != NetMessageLibraryType.Error);

			NetSending send = new NetSending(msg, NetMessageType.Library, 0);

			msg.m_wasSent = true;
			msg.m_numUnfinishedSendings++;
			m_unsentMessages.Enqueue(send);
		}

		/// <summary>
		/// Creates a new message for sending
		/// </summary>
		public NetOutgoingMessage CreateMessage()
		{
			return m_owner.CreateMessage();
		}

		/// <summary>
		/// Creates a new message for sending
		/// </summary>
		/// <param name="initialCapacity">initial capacity in bytes</param>
		public NetOutgoingMessage CreateMessage(int initialCapacity)
		{
			return m_owner.CreateMessage(initialCapacity);
		}

		public bool SendMessage(NetOutgoingMessage msg, NetDeliveryMethod method)
		{
			return SendMessage(msg, method, 0);
		}

		public bool SendMessage(NetOutgoingMessage msg, NetDeliveryMethod method, int sequenceChannel)
		{
			if (msg == null)
				throw new ArgumentNullException("msg");

			NetException.Assert(msg.m_libType == NetMessageLibraryType.Error, "Use SendLibrary() instead!");

			if (msg.IsSent)
				throw new NetException("Message has already been sent!");

			switch (method)
			{
				case NetDeliveryMethod.Unreliable:
				case NetDeliveryMethod.ReliableUnordered:
					if (sequenceChannel != 0)
						throw new NetException("Delivery method " + method + " cannot use sequence channels other than 0!");
					break;
				case NetDeliveryMethod.ReliableOrdered:
				case NetDeliveryMethod.ReliableSequenced:
				case NetDeliveryMethod.UnreliableSequenced:
					NetException.Assert(sequenceChannel >= 0 && sequenceChannel < NetConstants.NetChannelsPerDeliveryMethod, "Sequence channel must be between 0 and NetConstants.NetChannelsPerDeliveryMethod (" + NetConstants.NetChannelsPerDeliveryMethod + ")");
					break;
				case NetDeliveryMethod.Unknown:
				default:
					throw new NetException("Bad delivery method!");
			}

			if (m_owner == null)
				return false; // we've been disposed

			msg.m_wasSent = true;

			NetMessageType tp = (NetMessageType)((int)method + sequenceChannel);
			return EnqueueSendMessage(msg, tp);
		}

		internal bool EnqueueSendMessage(NetOutgoingMessage msg, NetMessageType tp)
		{			
			int msgLen = msg.LengthBytes;
			int mtu = m_owner.m_configuration.m_maximumTransmissionUnit;

			if (msgLen <= mtu)
			{
				NetSending send = new NetSending(msg, tp, GetSendSequenceNumber(tp));
				msg.m_numUnfinishedSendings++;

				send.SetNextResend(this);
				
				m_unsentMessages.Enqueue(send);
				return true;
			}

#if DEBUG
			if (tp < NetMessageType.UserReliableUnordered)
			{
				// unreliable
				m_owner.LogWarning("Sending more than MTU (currently " + mtu + ") bytes unreliably is not recommended!");
			}
#endif
			mtu -= NetConstants.FragmentHeaderSize; // size of fragmentation info

			// message must be fragmented
			int fgi = Interlocked.Increment(ref m_nextFragmentGroupId);
			// TODO: loop group id?

			int numFragments = (msgLen + mtu - 1) / mtu;

			for (int i = 0; i < numFragments; i++)
			{
				int flen = (i == numFragments - 1 ? (msgLen - (mtu * (numFragments - 1))) : mtu);

				NetSending fs = new NetSending(msg, tp, GetSendSequenceNumber(tp));
				fs.FragmentGroupId = fgi;
				fs.FragmentNumber = i;
				fs.FragmentTotalCount = numFragments;
				msg.m_numUnfinishedSendings++;
				m_unsentMessages.Enqueue(fs);
				fs.SetNextResend(this);
			}

			return true;
		}

		public void Disconnect(string byeMessage)
		{
			// called on user thread (possibly)
			if (m_status == NetConnectionStatus.None || m_status == NetConnectionStatus.Disconnected)
				return;

			m_owner.LogVerbose("Disconnect requested for " + this);
			m_disconnectByeMessage = byeMessage;

			if (m_status != NetConnectionStatus.Disconnected && m_status != NetConnectionStatus.None)
				SetStatus(NetConnectionStatus.Disconnecting, byeMessage);

			// loosen up throttling
			m_throttleDebt = -m_owner.m_configuration.m_throttlePeakBytes;

			// instantly resend all unacked
			double now = NetTime.Now;
			foreach(NetSending send in m_unackedSends)
				send.NextResend = now;

			NetOutgoingMessage bye = m_owner.CreateLibraryMessage(NetMessageLibraryType.Disconnect, byeMessage);
			SendLibrary(bye);
		}

		public void Approve()
		{
			if (!m_peerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.ConnectionApproval))
				m_owner.LogError("Approve() called but ConnectionApproval is not enabled in NetPeerConfiguration!");

			if (m_pendingStatus != PendingConnectionStatus.Pending)
			{
				m_owner.LogWarning("Approve() called on non-pending connection!");
				return;
			}
			m_pendingStatus = PendingConnectionStatus.Approved;
		}

		public void Deny(string reason)
		{
			if (!m_peerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.ConnectionApproval))
				m_owner.LogError("Deny() called but ConnectionApproval is not enabled in NetPeerConfiguration!");

			if (m_pendingStatus != PendingConnectionStatus.Pending)
			{
				m_owner.LogWarning("Deny() called on non-pending connection!");
				return;
			}
			m_pendingStatus = PendingConnectionStatus.Denied;
			m_pendingDenialReason = reason;
		}

		public override string ToString()
		{
			return "[NetConnection to " + m_remoteEndpoint + " Status: " + m_visibleStatus + "]";
		}
	}
}