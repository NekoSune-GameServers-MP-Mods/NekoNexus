/*
 * Helpers for the 1.0.2 (UberStrike 4.3.10, Android) web services.
 *
 * 4.3.10 identifies the player by CMID in every call; the 2.0 (PC) handlers take the player's
 * session token instead. Where a 2.0 handler already implements a feature, the 1.0.2 method
 * looks up the session the 1.0.2 login created and runs the 2.0 handler with it, so both client
 * versions share one implementation, one database and the same realtime notifications.
 */

import { GameSession } from '@festivaldev/nekonexus-models';
import { Op } from 'sequelize';

/** The live session token for a CMID ('' when the player has no session - the 2.0 handler then does nothing). */
export async function sessionTokenFor(cmid: number): Promise<string> {
  const session = await GameSession.findOne({
    where: { Cmid: cmid, ExpireTime: { [Op.gt]: new Date() } },
    order: [['ExpireTime', 'DESC']],
  });
  // Using the session keeps it alive (sliding 12h expiry), like an authenticated 2.0 call.
  session?.extendExpireTime();
  return session?.SessionId ?? '';
}

/** Run a 2.0 handler with plain (unencrypted) input and return its plain output ([] when it failed). */
export async function callV2(service: any, method: string, write: (input: number[]) => void): Promise<number[]> {
  const input: number[] = [];
  write(input);
  const output = await service[method](input, []);
  return output ?? [];
}

/**
 * Decryption, encryption and error handling for a 1.0.2 method; `body` reads the request from
 * `bytes` and writes the reply to `out`. Call as `bridge(this, name, data, outputStream, ...)`.
 */
export async function bridge(
  service: any,
  name: string,
  data: number[],
  outputStream: number[],
  body: (bytes: number[], out: number[]) => Promise<void>,
): Promise<number[] | null> {
  const isEncrypted = service.isEncrypted(data);
  const bytes = isEncrypted
    ? service.CryptoPolicy.RijndaelDecrypt(data, service.EncryptionPassPhrase, service.EncryptionInitVector)
    : data;
  try {
    service.debugEndpoint?.(name);
    await body(bytes, outputStream);
    return isEncrypted
      ? service.CryptoPolicy.RijndaelEncrypt(outputStream, service.EncryptionPassPhrase, service.EncryptionInitVector)
      : outputStream;
  } catch (error) {
    service.handleEndpointError(name, error);
  }
  return null;
}
