using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine;

public class WolverineMessageExpectationException : Exception
{
    public WolverineMessageExpectationException(string message, IReadOnlyList<object> messages) : base(message)
    {
        Messages = messages.ToArray();
    }

    public IReadOnlyList<object> Messages { get; }
}

public static class TestingExtensions
{
    private static object toMessage(this object message)
    {
        if (message is Envelope e)
        {
            return e.Message;
        }

        return message;
    }

    private static object[] resolveMessages(this IEnumerable<object> messages)
    {
        return messages.Select(x => x.toMessage()).Where(x => x != null)
            .ToArray();
    }

    private static string toListOfMessages(this IEnumerable<object> messages)
    {
        var actual = messages
            .resolveMessages();

        if (!actual.Any())
        {
            return "[no messages]";
        }

        return actual
            .Select(x => x.ToString()).Join(", ");
    }

    /// <summary>
    ///     Find the first envelope of the specified message type. Will throw if
    ///     no matching envelope is found
    /// </summary>
    /// <param name="envelopes"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static Envelope FindForMessageType<T>(this IEnumerable<Envelope> envelopes)
    {
        return envelopes.FirstOrDefault(x => x.Message is T) ?? throw new WolverineMessageExpectationException(
            $"Could not find an envelope with message type {typeof(T).FullNameInCode()}. The actual messages were {envelopes.toListOfMessages()}",
            envelopes.resolveMessages());
    }

    /// <summary>
    ///     Assert that there are no messages of any type within this published collection
    /// </summary>
    /// <param name="messages"></param>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static void ShouldHaveNoMessages(this IEnumerable<object>? messages)
    {
        if (messages == null)
        {
            return;
        }

        if (messages.Any())
        {
            throw new WolverineMessageExpectationException(
                $"Should be no messages, but was {messages.toListOfMessages()}", messages.ToArray());
        }
    }

    /// <summary>
    ///     Assert that no messages of type T were part of this collection
    /// </summary>
    /// <param name="messages"></param>
    /// <typeparam name="T"></typeparam>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static void ShouldHaveNoMessageOfType<T>(this IEnumerable<object> messages)
    {
        var actual = messages.resolveMessages();
        if (actual.OfType<T>().Any())
        {
            throw new WolverineMessageExpectationException(
                $"Should be no messages of type {typeof(T).FullNameInCode()}, but the actual messages were {actual.toListOfMessages()}",
                actual);
        }
    }

    /// <summary>
    ///     Assert and return the first message of type T within this collection
    ///     of published messages
    /// </summary>
    /// <param name="messages"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static T ShouldHaveMessageOfType<T>(this IEnumerable<object> messages)
    {
        var actual = messages.resolveMessages();
        if (!actual.Any())
        {
            throw new WolverineMessageExpectationException(
                $"Should be a message of type {typeof(T).FullNameInCode()}, but there were no messages", actual);
        }

        return actual.OfType<T>().FirstOrDefault() ?? throw new WolverineMessageExpectationException(
            $"Should be a message of type {typeof(T).FullNameInCode()}, but actual messages were {actual.toListOfMessages()}",
            actual);
    }

    /// <summary>
    ///     If it exists, find the first envelope that contains a message of type T
    ///     within this collection of published messages. This is only necessary for
    ///     testing customized message sending (explicit destination, headers, scheduled delivery)
    /// </summary>
    /// <param name="messages"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static Envelope ShouldHaveEnvelopeForMessageType<T>(this IEnumerable<object> messages)
    {
        return messages.OfType<Envelope>()
            .FirstOrDefault(x => x.Message is T) ?? throw new WolverineMessageExpectationException(
            $"Unable to find an envelope for type {typeof(T).FullNameInCode()}, actual messages were {messages.toListOfMessages()}",
            messages.ToArray());
    }
}