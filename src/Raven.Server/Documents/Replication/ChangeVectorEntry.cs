using System;
using System.Text;
using Raven.Client.Extensions;

namespace Raven.Server.Documents.Replication
{
    public struct ChangeVectorEntry : IComparable<ChangeVectorEntry>
    {
        public Guid DbId;
        public long Etag;
        public int NodeTag;
        public void Append(StringBuilder sb)
        {
            ChangeVectorExtensions.ToBase26(sb, NodeTag);
            sb.Append(":");
            sb.Append(Etag);
            sb.Append("-");
            //TODO: Fix this allocation mess
            sb.Append(Convert.ToBase64String(DbId.ToByteArray()));
        }
        
        public override string ToString()
        {
            var sb = new StringBuilder();
            Append(sb);
            return sb.ToString();
        }
        public bool Equals(ChangeVectorEntry other)
        {
            return DbId.Equals(other.DbId) && Etag == other.Etag;
        }
        public override int GetHashCode()
        {
            unchecked
            {
                return (DbId.GetHashCode() * 397) ^ Etag.GetHashCode();
            }
        }
        // we use it to sort change vectors by the ID.
        public int CompareTo(ChangeVectorEntry other)
        {
            var rc = DbId.CompareTo(other.DbId);
            if (rc != 0)
                return rc;
            return Etag.CompareTo(other.Etag);
        }
    }
}